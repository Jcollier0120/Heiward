// /*
//     Copyright (C) 2026 Jeremy Collier
//     This file is part of VideoDuplicateFinder
//     VideoDuplicateFinder is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
//     VideoDuplicateFinder is distributed in the hope that it will be useful,
//     but WITHOUT ANY WARRANTY without even the implied warranty of
//     MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//     GNU Affero General Public License for more details.
//     You should have received a copy of the GNU Affero General Public License
//     along with VideoDuplicateFinder.  If not, see <http://www.gnu.org/licenses/>.
// */
//

using System.Text.Json;
using Jint;

namespace HEI.Agent.Tests;

/// <summary>
/// Call of Duty's security check (attestation), before the game finds it: only its titles that check get a warning, on their
/// page, naming each requirement this PC doesn't meet and what to do, and each Heiward couldn't read as not checked, never as
/// met. The PCs here are what a test says: only <see cref="PlatformReads_ThisPC_ReadsWithoutThrowing"/> reads this PC, and only reads.
/// </summary>
public sealed class CodAttestationTests {
	internal static readonly AttestationSignals AllMet = new(26100, Uefi: true, SecureBoot: true, Gpt: true, new TpmReading(true, "2.0", "INTC", "600.18.32.2473"));

	static GameInstall Game(string name, string launcher = GameLaunchers.Steam) =>
		new(GameLibraries.IdOf(launcher, name), launcher, name, name, @"X:\Games\" + name, null, 100L << 30, null, null);

	static GameWarning Warn(AttestationSignals s) => Assert.IsType<GameWarning>(CodAttestation.Warning(s));

	static GameWarningLine Only(GameWarning w, string state) => Assert.Single(w.Lines, l => l.State == state);

	// ------------------------------------------------------------------ which games

	[Theory]
	[InlineData("Call of Duty®")]                 // Steam's and the Xbox app's launcher for Black Ops 7 and Warzone
	[InlineData("Call of Duty")]                  // Battle.net's
	[InlineData("Call of Duty HQ")]
	[InlineData("Call of Duty®: Black Ops 7")]
	[InlineData("Call of Duty: Black Ops VII")]
	[InlineData("Call of Duty®: Warzone™")]
	[InlineData("Call of Duty: Modern Warfare 4")]
	[InlineData("Call of Duty: Modern Warfare IV")]
	public void Needs_TheTitlesThatCheck(string name) => Assert.True(CodAttestation.Needs(name));

	[Theory]
	[InlineData("Call of Duty: Black Ops III")]
	[InlineData("Call of Duty: Black Ops 6")]
	[InlineData("Call of Duty: Black Ops Cold War")]
	[InlineData("Call of Duty®: Modern Warfare® III")]
	[InlineData("Call of Duty 4: Modern Warfare")]
	[InlineData("Call of Duty: WWII")]
	[InlineData("Call of Duty: Black Ops 70")]
	[InlineData("Call of Juarez")]
	[InlineData("Warzone")]
	[InlineData("Duty Calls")]
	public void Needs_NotOlderTitles_OrOtherGames(string name) => Assert.False(CodAttestation.Needs(name));

	[Fact]
	public void Mark_ReadsThePC_OnlyForATitleThatChecks_AndOnce() {
		int reads = 0;
		var games = new List<GameInstall> { Game("Call of Duty®"), Game("Fortnite", GameLaunchers.Epic), Game("Call of Duty", GameLaunchers.BattleNet) };
		var marked = CodAttestation.Mark(games, () => { reads++; return AllMet with { SecureBoot = false }; });
		Assert.Equal(1, reads);
		Assert.NotNull(marked[0].Warning);
		Assert.Null(marked[1].Warning);
		Assert.Equal(marked[0].Warning, marked[2].Warning);

		var others = new List<GameInstall> { Game("Fortnite", GameLaunchers.Epic), Game("Call of Duty: Black Ops III") };
		Assert.Same(others, CodAttestation.Mark(others, () => { reads++; return AllMet; }));
		Assert.Equal(1, reads);
	}

	[Fact]
	public void Mark_APCThatMeetsItAll_GetsNoWarning() =>
		Assert.Null(CodAttestation.Mark([Game("Call of Duty®")], () => AllMet)[0].Warning);

	// ------------------------------------------------------------------ the requirements

	[Fact]
	public void AllCheckedAndMet_NothingShown() => Assert.Null(CodAttestation.Warning(AllMet));

	[Fact]
	public void SecureBootOff_InUefiMode() {
		GameWarning w = Warn(AllMet with { SecureBoot = false });
		GameWarningLine line = Assert.Single(w.Lines);
		Assert.Equal(CodAttestation.Unmet, line.State);
		Assert.Equal("Secure Boot is off", line.Name);
		Assert.Contains("Turn on Secure Boot in your PC's firmware settings", line.Fix);
		Assert.Equal("This PC doesn't meet Call of Duty's security requirements", w.Title);
	}

	[Fact]
	public void LegacyOnMbr_TheDiskFirst_BackUp_AndHeiwardNeverConvertsIt() {
		GameWarning w = Warn(AllMet with { Uefi = false, Gpt = false, SecureBoot = null });
		Assert.Equal(new[] { "The Windows disk is MBR, not GPT", "Windows starts in Legacy (CSM) mode, not UEFI", "Secure Boot is off" }, w.Lines.Select(l => l.Name));
		Assert.All(w.Lines, l => Assert.Equal(CodAttestation.Unmet, l.State));
		string disk = w.Lines[0].Fix;
		Assert.StartsWith("Back up everything you can't lose first", disk);
		Assert.Contains("can't be undone", disk);
		Assert.Contains("switch the firmware settings from Legacy or CSM to UEFI, or Windows won't start", disk);
		Assert.Contains("Heiward never converts a disk.", disk);
		Assert.Contains("only once the Windows disk is GPT", w.Lines[1].Fix);
		Assert.Equal("It can only be on when Windows starts in UEFI mode", w.Lines[2].Detail);
	}

	[Fact]
	public void LegacyMode_SecureBootIsOff_EvenWithoutTheRegistrysWord() {
		GameWarning w = Warn(AllMet with { Uefi = false, SecureBoot = null });
		Assert.Equal(CodAttestation.Unmet, w.Lines.Single(l => l.Name == "Secure Boot is off").State);
		Assert.DoesNotContain(w.Lines, l => l.State == CodAttestation.Unchecked);
	}

	[Fact]
	public void NothingReadable_EachSaidNotChecked_NeverMet() {
		GameWarning w = Warn(new AttestationSignals(26100, null, null, null, null));
		Assert.Equal(new[] { "TPM 2.0", "The Windows disk's partition style", "Boot mode", "Secure Boot" }, w.Lines.Select(l => l.Name));
		Assert.All(w.Lines, l => Assert.Equal(CodAttestation.Unchecked, l.State));
		Assert.Equal("Heiward couldn't check all of Call of Duty's security requirements", w.Title);
		Assert.Contains("tpm.msc", w.Lines[0].Fix);
		Assert.Contains("diskmgmt.msc", w.Lines[1].Fix);
		Assert.Contains("BIOS Mode should say UEFI", w.Lines[2].Fix);
		Assert.Contains("Secure Boot State should say On", w.Lines[3].Fix);
	}

	[Fact]
	public void NoTpm_TurnItOnInTheFirmware_AndNoFirmwareLine() {
		GameWarning w = Warn(AllMet with { Tpm = new TpmReading(false, null, null, null) });
		GameWarningLine line = Assert.Single(w.Lines);
		Assert.Equal((CodAttestation.Unmet, "No TPM 2.0 found"), (line.State, line.Name));
		Assert.Contains("fTPM on AMD", line.Fix);
		Assert.Contains("PTT or Platform Trust Technology on Intel", line.Fix);
	}

	[Fact]
	public void Tpm12_NotEnough() =>
		Assert.Equal("The TPM is version 1.2", Only(Warn(AllMet with { Tpm = new TpmReading(true, "1.2", null, null) }), CodAttestation.Unmet).Name);

	[Theory]
	[InlineData("3.87.0.5")]
	[InlineData("3.5.0.1")]
	public void AmdFirmware3x0x_DoesntPass(string firmware) {
		GameWarningLine line = Only(Warn(AllMet with { Tpm = new TpmReading(true, "2.0", "AMD", firmware) }), CodAttestation.Unmet);
		Assert.Equal("The TPM's firmware needs an update", line.Name);
		Assert.Contains(firmware, line.Detail);
		Assert.Contains("BIOS update", line.Fix);
	}

	[Theory]
	[InlineData("302.12.0.0")]
	[InlineData("303.12.32.1")]
	public void IntelFirmware302Or303_12_MayNeedAnUpdate(string firmware) {
		GameWarning w = Warn(AllMet with { Tpm = new TpmReading(true, "2.0", "INTC", firmware) });
		GameWarningLine line = Assert.Single(w.Lines);
		Assert.Equal((CodAttestation.Advised, "The TPM's firmware may need an update"), (line.State, line.Name));
		Assert.Equal("Call of Duty may ask for a firmware update on this PC", w.Title);
	}

	[Theory]
	[InlineData("AMD", "6.32.0.4")]
	[InlineData("AMD", "3.87.1.5")]
	[InlineData("INTC", "600.18.32.2473")]
	[InlineData("INTC", "302.120.0.0")]
	[InlineData("INTC", "3.87.0.5")]
	[InlineData("IFX", "302.12.0.0")]
	public void OtherFirmware_Passes(string maker, string firmware) =>
		Assert.Null(CodAttestation.Warning(AllMet with { Tpm = new TpmReading(true, "2.0", maker, firmware) }));

	[Fact]
	public void FirmwareUnread_NotChecked() {
		GameWarningLine line = Only(Warn(AllMet with { Tpm = new TpmReading(true, "2.0", null, null) }), CodAttestation.Unchecked);
		Assert.Equal("The TPM's firmware version", line.Name);
		Assert.Contains("Manufacturer Version", line.Fix);
	}

	[Fact]
	public void AnAdvisedUpdate_OutranksWhatWasntChecked_InTheTitle() =>
		Assert.Equal("Call of Duty may ask for a firmware update on this PC",
			Warn(AllMet with { Gpt = null, Tpm = new TpmReading(true, "2.0", "INTC", "302.12.0.0") }).Title);

	[Theory]
	[InlineData(19044, true)]
	[InlineData(19045, false)]
	[InlineData(22631, false)]
	public void Windows10_22H2OrLater(int build, bool unmet) {
		GameWarning? w = CodAttestation.Warning(AllMet with { WindowsBuild = build });
		Assert.Equal(unmet, w != null);
		if (w != null) Assert.Contains($"build {build}", Assert.Single(w.Lines).Detail);
	}

	/// <summary>Every line the check can show: its detail without a full stop (the page adds it), a fix that ends with one, and no maker's detail.</summary>
	[Fact]
	public void EveryLine_ReadsAsTheCustomerSeesIt() {
		AttestationSignals[] pcs = [
			new(19041, false, false, false, new TpmReading(true, "1.2", null, null)),
			new(19041, null, null, null, null),
			AllMet with { SecureBoot = false, Tpm = new TpmReading(false, null, null, null) },
			AllMet with { Tpm = new TpmReading(true, "2.0", "AMD", "3.87.0.5") },
			AllMet with { Tpm = new TpmReading(true, "2.0", "INTC", "303.12.0.0") },
			AllMet with { Tpm = new TpmReading(true, "2.0", null, null) },
			AllMet with { Tpm = new TpmReading(true, null, null, null) },
		];
		var warnings = pcs.Select(Warn).ToList();
		foreach (GameWarningLine l in warnings.SelectMany(w => w.Lines)) {
			Assert.False(l.Detail.EndsWith('.'), l.Detail);
			Assert.EndsWith(".", l.Fix);
			Assert.Contains(l.State, new[] { CodAttestation.Unmet, CodAttestation.Advised, CodAttestation.Unchecked });
		}
		string all = string.Join("\n", warnings.SelectMany(w => new[] { w.Title, w.Intro, w.Outro }.Concat(w.Lines.SelectMany(l => new[] { l.Name, l.Detail, l.Fix }))));
		foreach (string maker in new[] { "Jcollier", "github", "Steward", "Castellan", "GAMING-", @"D:\", "Worktrees", "Snapdragon" })
			Assert.DoesNotContain(maker, all, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("changes nothing on this PC", warnings[0].Outro);
		Assert.Contains("BitLocker recovery key", warnings[0].Outro);
	}

	// ------------------------------------------------------------------ reading the PC

	static byte[] Answer(uint code, params (uint Property, uint Value)[] properties) {
		var b = new List<byte>();
		void W(uint v) => b.AddRange(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });
		b.AddRange(new byte[] { 0x80, 0x01 });
		W((uint)(19 + 8 * properties.Length));
		W(code);
		b.Add(0); // no more data
		W(6);     // TPM_CAP_TPM_PROPERTIES
		W((uint)properties.Length);
		foreach (var (p, v) in properties) { W(p); W(v); }
		return b.ToArray();
	}

	[Fact]
	public void TpmProperties_AsTpmMscShowsThem() {
		Assert.Equal(("AMD", "3.87.0.5"), PlatformReads.Properties(Answer(0, (0x105, 0x414D4400), (0x106, 0), (0x10B, 0x00030057), (0x10C, 0x00000005))));
		Assert.Equal(("INTC", "302.12.0.0"), PlatformReads.Properties(Answer(0, (0x105, 0x494E5443), (0x10B, 0x012E000C), (0x10C, 0))));
		Assert.Equal(("IFX", null), PlatformReads.Properties(Answer(0, (0x105, 0x49465800))));
	}

	[Fact]
	public void TpmProperties_AnErrorOrACutAnswer_ReadsAsNothing() {
		byte[] good = Answer(0, (0x105, 0x494E5443), (0x10B, 0x012E000C), (0x10C, 0));
		Assert.Equal((null, null), PlatformReads.Properties(Answer(0x101, (0x105, 0x494E5443))));
		Assert.Equal((null, null), PlatformReads.Properties(good.AsSpan(0, 12)));
		Assert.Equal(("INTC", null), PlatformReads.Properties(good.AsSpan(0, good.Length - 8)));
	}

	[Fact]
	public void TpmCommand_IsGetCapabilityOfTheFixedProperties() {
		byte[] c = PlatformReads.GetProperties;
		Assert.Equal(c.Length, c[2] << 24 | c[3] << 16 | c[4] << 8 | c[5]);
		Assert.Equal(new byte[] { 0x00, 0x00, 0x01, 0x7A }, c[6..10]);
		Assert.Equal(new byte[] { 0x00, 0x00, 0x01, 0x05 }, c[14..18]);
	}

	[Theory]
	[InlineData("1", true)]
	[InlineData("0", false)]
	[InlineData(null, null)]
	public void SecureBoot_FromTheRegistry(string? value, bool? on) {
		var registry = new FakeRegistry();
		if (value != null) registry.Set(PlatformReads.SecureBootKey, "UEFISecureBootEnabled", value);
		Assert.Equal(on, PlatformReads.SecureBoot(registry));
	}

	/// <summary>This PC's own reads, as Heiward makes them without administrator rights: they answer, or say they couldn't, and never throw.</summary>
	[Fact]
	public void PlatformReads_ThisPC_ReadsWithoutThrowing() {
		AttestationSignals s = PlatformReads.Read();
		Assert.True(s.WindowsBuild > 0);
		if (s.Tpm is { Present: true, Version: "2.0", Firmware: { } firmware }) Assert.Matches(@"^\d+\.\d+\.\d+\.\d+$", firmware);
	}

	// ------------------------------------------------------------------ the games check and its report

	[Fact]
	public void GamesCheck_PutsTheWarningOnTheCallOfDutyGame_Only() {
		using var pc = new FakeGamePC();
		pc.SteamManifest(pc.Steam, "1938090", "Call of Duty®", "Call of Duty HQ");
		pc.File(pc.P("PF86", "Steam", "steamapps", "common", "Call of Duty HQ", "cod.exe"));
		int reads = 0;
		pc.Attestation = () => { reads++; return AllMet with { SecureBoot = false }; };
		GameReport report = GameScanner.Run(pc.Places);
		Assert.Equal(1, reads);
		GameInstall cod = report.Games.Single(g => g.AppId == "1938090");
		Assert.Equal("Secure Boot is off", Assert.Single(cod.Warning!.Lines).Name);
		Assert.All(report.Games.Where(g => g != cod), g => Assert.Null(g.Warning));
		// Nothing of it is a thing to remove: no category of its own.
		Assert.All(report.Categories, c => Assert.Contains(c.Key, new[] { GameScanner.Leftovers, GameScanner.Caches, GameScanner.Shaders, GameScanner.Dumps, GameScanner.Twice, GameScanner.Idle }));

		var back = JsonSerializer.Deserialize<GameReport>(JsonSerializer.Serialize(report, AgentConfig.Json), AgentConfig.Json)!;
		Assert.Equal(cod.Warning, back.Games.Single(g => g.AppId == "1938090").Warning with { Lines = cod.Warning.Lines });
		Assert.Equal(cod.Warning.Lines, back.Games.Single(g => g.AppId == "1938090").Warning!.Lines);
		Assert.Contains("\"warning\":", JsonSerializer.Serialize(report, AgentConfig.Json));
	}

	[Fact]
	public void GamesCheck_WithoutCallOfDuty_ReadsNothing() {
		using var pc = new FakeGamePC();
		pc.Attestation = () => throw new InvalidOperationException("read without a title that checks");
		Assert.All(GameScanner.Run(pc.Places).Games, g => Assert.Null(g.Warning));
	}

	// ------------------------------------------------------------------ the page

	const string FakeDom = """
		function makeEl(tag) {
		  return { tagName: tag.toUpperCase(), children: [], className: '', textContent: '', title: '',
		    append: function () { for (var i = 0; i < arguments.length; i++) this.children.push(arguments[i]); } };
		}
		var document = { createElement: makeEl };
		function icon(name) { var e = makeEl('svg'); e.icon = name; return e; }
		// Each piece of text the element shows, with its class, in order.
		function texts(e) { var out = []; (function walk(n) { if (n.textContent) out.push(n.className + '|' + n.textContent); n.children.forEach(walk); })(e); return out; }
		""";

	static Engine PageEngine() {
		string script = ThePage.Asset("app.js").ReplaceLineEndings("\n");
		var js = new Engine();
		js.Execute(FakeDom);
		js.Execute(ThePage.Function(script, "el"));
		int at = script.IndexOf("const WARNING_TAGS = {", StringComparison.Ordinal);
		Assert.True(at >= 0, "WARNING_TAGS");
		js.Execute(script[at..(script.IndexOf("\n};", at, StringComparison.Ordinal) + 3)]);
		js.Execute(ThePage.Function(script, "gameTileWarning"));
		js.Execute(ThePage.Function(script, "gameWarningSection"));
		return js;
	}

	[Fact]
	public void ThePage_AGamesWarning_EachLineWithItsTagAndWhatToDo() {
		GameWarning w = Warn(AllMet with { SecureBoot = false, Gpt = null, Tpm = new TpmReading(true, "2.0", "INTC", "302.12.0.0") });
		Engine js = PageEngine();
		js.SetValue("warning", JsonSerializer.Serialize(w, AgentConfig.Json));
		string[] shown = JsonSerializer.Deserialize<string[]>(js.Evaluate("JSON.stringify(texts(gameWarningSection(JSON.parse(warning))))").AsString())!;
		Assert.Equal(new[] {
			"|" + w.Title,
			"muted small|" + w.Intro,
			"|The TPM's firmware may need an update", "tag advised|Update advised", "muted small|" + w.Lines[0].Detail + ".", "small game-removing|" + w.Lines[0].Fix,
			"|The Windows disk's partition style", "tag|Not checked", "muted small|Heiward couldn't read it on this PC.", "small game-removing|" + w.Lines[1].Fix,
			"|Secure Boot is off", "tag unmet|Not met", "muted small|Call of Duty needs it on.", "small game-removing|" + w.Lines[2].Fix,
			"muted small|" + w.Outro,
		}, shown);
	}

	[Fact]
	public void ThePage_TheTileSaysSo_OnlyForWhatsNotMetOrAdvised() {
		Engine js = PageEngine();
		string Tile(AttestationSignals s) {
			js.SetValue("warning", JsonSerializer.Serialize(CodAttestation.Warning(s), AgentConfig.Json));
			return js.Evaluate("String(gameTileWarning(JSON.parse(warning)))").AsString();
		}
		Assert.Equal("Fails its security check", Tile(AllMet with { SecureBoot = false, Tpm = new TpmReading(true, "2.0", "INTC", "302.12.0.0") }));
		Assert.Equal("TPM update advised", Tile(AllMet with { Gpt = null, Tpm = new TpmReading(true, "2.0", "INTC", "302.12.0.0") }));
		Assert.Equal("null", Tile(AllMet with { Gpt = null }));
		Assert.Equal("null", Tile(AllMet));
	}

	[Fact]
	public void ThePage_TheWarningIsFirstOnTheGamesPage_AndUnderItsTile() {
		string script = ThePage.Asset("app.js");
		string page = ThePage.Function(script, "gamePage");
		Assert.Contains("if (game.warning) out.push(gameWarningSection(game.warning));", page);
		Assert.True(page.IndexOf("gameWarningSection", StringComparison.Ordinal) < page.IndexOf("const mine = gameItems()", StringComparison.Ordinal));
		string tile = ThePage.Function(script, "gameTile");
		Assert.Contains("gameTileWarning(game.warning)", tile);
		Assert.Contains("line.title = game.warning.title;", tile);
		string css = ThePage.Asset("app.css");
		foreach (string rule in new[] { ".game-tile-warn {", ".tag.unmet {", ".tag.advised {" }) Assert.Contains(rule, css);
	}
}
