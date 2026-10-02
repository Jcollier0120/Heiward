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

using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HEI.Core.Utils;

namespace HEI.Core.AI {
	/// <summary>
	/// The devices the manor's programs run their models on, as every one of them names them (Manor's
	/// docs/ACCELERATORS.md): <c>npu</c>, <c>cpu</c>, or a graphics card, <c>gpu-</c> and its name slugged
	/// (<see cref="GpuId"/>). Each has a lock and a line every program shares (<see cref="NpuLock"/>).
	/// <para>
	/// Heiward's failure markers (<c>&lt;id&gt;.failed.json</c>, in the shared files' format) are its own, in
	/// <c>{ai}\accelerators</c>: the manor's shared markers in <c>.npu-agent\accelerators</c> describe a model server
	/// failing (GenieX, llama-server, npu-embed), and Heiward runs its model in its own process (QNN or DirectML), so
	/// it neither writes nor reads nor deletes those. Its own it skips for 10 minutes; a success on it deletes it.
	/// </para>
	/// </summary>
	public static partial class Accelerators {
		public const string Npu = "npu", Cpu = "cpu";
		/// <summary>Who Heiward is in tickets and failure markers.</summary>
		public const string Who = "heiward";
		/// <summary>A failed accelerator is skipped this long after its marker's <c>since</c>, then tried again.</summary>
		public static readonly TimeSpan FailedFor = TimeSpan.FromMinutes(10);
		/// <summary>A marker's reason is one line, this long at most (as the manor's shared markers have it).</summary>
		const int ReasonMax = 300;

		/// <summary>A card's id: <c>gpu-</c> and its name (<see cref="GpuAdapter.Key"/>) slugged, <c>gpu-graphics-card</c> for an empty one.</summary>
		public static string GpuId(string cardKey) => "gpu-" + (Slug(cardKey) is { Length: > 0 } s ? s : "graphics-card");

		/// <summary>
		/// A name as the manor's ids have it: lowercase, each run of characters other than a-z and 0-9 one dash, none
		/// at either end ("NVIDIA GeForce RTX 4090 #2" is "nvidia-geforce-rtx-4090-2"). As the TypeScript programs do it:
		/// <c>name.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '')</c>.
		/// </summary>
		public static string Slug(string name) => NotIdChars().Replace(name.ToLowerInvariant(), "-").Trim('-');

		[GeneratedRegex("[^a-z0-9]+", RegexOptions.CultureInvariant)]
		private static partial Regex NotIdChars();

		[GeneratedRegex("^(npu|cpu|gpu-[a-z0-9]+(-[a-z0-9]+)*)$", RegexOptions.CultureInvariant)]
		private static partial Regex IdPattern();

		/// <summary>An accelerator id: npu, cpu or gpu-… (nothing that could name another folder).</summary>
		public static bool IsId(string? id) => id != null && IdPattern().IsMatch(id);

		/// <summary>"npu", "gpu" or "cpu": the kind an id names.</summary>
		public static string KindOf(string id) => id == Npu ? "npu" : id == Cpu ? "cpu" : "gpu";

		/// <summary>Tests point the markers' folder elsewhere.</summary>
		internal static string? FolderOverride;

		/// <summary>
		/// Heiward's own failure markers: <c>{ai}\accelerators</c>, beside its AI components. Never the manor's shared
		/// <c>.npu-agent\accelerators</c>, whose markers are about model servers, not Heiward's in-process runtimes.
		/// </summary>
		public static string Folder => FolderOverride ?? Path.Combine(AiComponents.AiFolder, "accelerators");

		public static string FailureFile(string id) => Path.Combine(Folder, id + ".failed.json");

		/// <summary>A failure marker: when it was written (UTC), why, and by whom (Heiward, in its own folder).</summary>
		public sealed record Failure(DateTime SinceUtc, string Reason, string By) {
			/// <summary>When it stops counting.</summary>
			public DateTime UntilUtc => SinceUtc + FailedFor;
		}

		/// <summary>
		/// The accelerator's failure marker while it counts (10 minutes from its <c>since</c>); null when there is none,
		/// it's unreadable (anything unreadable counts as absent), or it's older.
		/// </summary>
		public static Failure? FailureOf(string id, DateTime? nowUtc = null) {
			if (!IsId(id)) return null;
			try {
				string path = FailureFile(id);
				if (!File.Exists(path)) return null;
				// Shared read, write and delete: a scan's writer renames over it, and a success deletes it, while the page reads.
				using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
				using JsonDocument doc = JsonDocument.Parse(file);
				JsonElement root = doc.RootElement;
				if (root.ValueKind != JsonValueKind.Object ||
					!root.TryGetProperty("since", out JsonElement s) || s.ValueKind != JsonValueKind.String ||
					!root.TryGetProperty("reason", out JsonElement r) || r.ValueKind != JsonValueKind.String ||
					!DateTime.TryParse(s.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime since))
					return null;
				since = DateTime.SpecifyKind(since, DateTimeKind.Utc);
				if ((nowUtc ?? DateTime.UtcNow) - since >= FailedFor) return null;
				string by = root.TryGetProperty("by", out JsonElement b) && b.ValueKind == JsonValueKind.String ? b.GetString()! : "";
				return new Failure(since, r.GetString()!, by);
			}
			catch { return null; }
		}

		/// <summary>
		/// Marks the accelerator failed now: <c>{ "since", "reason", "by": "heiward" }</c>, written whole (a temporary file, then a
		/// rename). Never throws: a marker that can't be written is logged and left.
		/// </summary>
		public static void MarkFailed(string id, string reason, DateTime? nowUtc = null) {
			if (!IsId(id)) return;
			string line = OneLine(reason);
			try {
				var json = new MemoryStream();
				using (var w = new Utf8JsonWriter(json, new JsonWriterOptions { Indented = true })) {
					w.WriteStartObject();
					w.WriteString("since", (nowUtc ?? DateTime.UtcNow).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
					w.WriteString("reason", line);
					w.WriteString("by", Who);
					w.WriteEndObject();
				}
				WriteWhole(FailureFile(id), json.ToArray());
				Logger.Instance.Warn($"Marked {id} failed for {FailedFor.TotalMinutes:N0} minutes: {line}");
			}
			catch (Exception e) {
				Logger.Instance.Info($"Couldn't mark {id} failed ({FailureFile(id)}): {e.Message}");
			}
		}

		/// <summary>A success on the accelerator: Heiward's failure marker for it goes.</summary>
		public static void Succeeded(string id) {
			if (!IsId(id)) return;
			try {
				string path = FailureFile(id);
				if (!File.Exists(path)) return;
				File.Delete(path);
				Logger.Instance.Info($"{id} works again: its failure marker is gone.");
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
		}

		/// <summary>The first line of <paramref name="text"/>, trimmed, at most <see cref="ReasonMax"/> characters.</summary>
		internal static string OneLine(string? text) {
			string line = (text ?? "").Trim();
			int end = line.IndexOfAny(['\r', '\n']);
			if (end >= 0) line = line[..end].TrimEnd();
			if (line.Length == 0) line = "it failed";
			return line.Length <= ReasonMax ? line : line[..ReasonMax];
		}

		/// <summary>A temporary file beside it, then a rename over it; Windows refuses the rename for a moment while a reader has it open.</summary>
		internal static void WriteWhole(string path, byte[] contents) {
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			string tmp = $"{path}.{Environment.ProcessId}.{Guid.NewGuid().ToString("N")[..6]}.tmp";
			File.WriteAllBytes(tmp, contents);
			for (int attempt = 0; ; attempt++) {
				try {
					File.Move(tmp, path, overwrite: true);
					return;
				}
				catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < 5) {
					Thread.Sleep(20 * (attempt + 1));
				}
				catch {
					try { File.Delete(tmp); } catch { }
					throw;
				}
			}
		}
	}

	/// <summary>
	/// Which graphics cards have run the AI model in a check: <c>hei probe --device gpu</c> (the installer's, or the one a scan
	/// runs first), or a scan that ran on the card. Auto takes a card only once it has passed one with its current driver and
	/// the GPU pack in use. Kept in <c>{ai}\gpu-checks.json</c>, one entry per card.
	/// </summary>
	public static class GpuChecks {
		/// <summary>A failed check is tried again after this long (a driver update makes a new check at once).</summary>
		public static readonly TimeSpan RetryFailedAfter = TimeSpan.FromDays(1);

		/// <param name="Card">The card's <see cref="GpuAdapter.Key"/>.</param>
		/// <param name="Driver">Its driver version when checked ("" when DXGI didn't say).</param>
		/// <param name="Pack">The GPU pack's ONNX Runtime DirectML version.</param>
		public sealed record Check(string Card, string Driver, string Pack, bool Passed, DateTime AtUtc, string? Reason);

		public static string FilePath => PathOverride ?? Path.Combine(AiComponents.AiFolder, "gpu-checks.json");
		/// <summary>Tests keep their checks elsewhere.</summary>
		internal static string? PathOverride;

		/// <summary>The card's check with its current driver and pack, or null when it has none.</summary>
		public static Check? Find(GpuAdapter card) =>
			Load().FirstOrDefault(c => string.Equals(c.Card, card.Key, StringComparison.OrdinalIgnoreCase) && c.Driver == card.Driver && c.Pack == GpuComponents.PackVersion);

		public static bool Passed(GpuAdapter card) => Find(card) is { Passed: true };

		/// <summary>Not checked with this driver and pack, or failed over a day ago.</summary>
		public static bool NeedsCheck(GpuAdapter card, DateTime nowUtc) =>
			Find(card) is not { } c || !c.Passed && nowUtc - c.AtUtc >= RetryFailedAfter;

		/// <summary>Records a check of <paramref name="card"/>, replacing its last one. Never throws.</summary>
		public static void Record(GpuAdapter card, bool passed, string? reason = null, DateTime? nowUtc = null) {
			try {
				List<Check> checks = Load().Where(c => !string.Equals(c.Card, card.Key, StringComparison.OrdinalIgnoreCase)).ToList();
				checks.Add(new Check(card.Key, card.Driver, GpuComponents.PackVersion, passed, (nowUtc ?? DateTime.UtcNow).ToUniversalTime(),
					passed ? null : Accelerators.OneLine(reason)));
				var json = new MemoryStream();
				using (var w = new Utf8JsonWriter(json, new JsonWriterOptions { Indented = true })) {
					w.WriteStartObject();
					w.WriteStartObject("cards");
					foreach (Check c in checks) {
						w.WriteStartObject(c.Card);
						w.WriteString("driver", c.Driver);
						w.WriteString("pack", c.Pack);
						w.WriteBoolean("passed", c.Passed);
						w.WriteString("checkedAt", c.AtUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
						if (c.Reason != null) w.WriteString("reason", c.Reason);
						else w.WriteNull("reason");
						w.WriteEndObject();
					}
					w.WriteEndObject();
					w.WriteEndObject();
				}
				Accelerators.WriteWhole(FilePath, json.ToArray());
			}
			catch (Exception e) {
				Logger.Instance.Info($"Couldn't record the check of the {card.Key}: {e.Message}");
			}
		}

		static List<Check> Load() {
			var checks = new List<Check>();
			try {
				if (!File.Exists(FilePath)) return checks;
				using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(FilePath));
				if (!doc.RootElement.TryGetProperty("cards", out JsonElement cards) || cards.ValueKind != JsonValueKind.Object) return checks;
				foreach (JsonProperty card in cards.EnumerateObject()) {
					JsonElement c = card.Value;
					if (c.ValueKind != JsonValueKind.Object) continue;
					string Text(string name) => c.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
					bool passed = c.TryGetProperty("passed", out JsonElement p) && p.ValueKind == JsonValueKind.True;
					DateTime at = DateTime.TryParse(Text("checkedAt"), CultureInfo.InvariantCulture,
						DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime t) ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : DateTime.MinValue;
					checks.Add(new Check(card.Name, Text("driver"), Text("pack"), passed, at, Text("reason") is { Length: > 0 } r ? r : null));
				}
			}
			catch { /* unreadable: nothing checked */ }
			return checks;
		}
	}

	/// <summary>
	/// Where an AI session goes, decided before anything loads ONNX Runtime (a process loads one, and the GPU needs its
	/// DirectML build): the device, the card for the GPU, and, when that isn't where the setting meant it to run, why.
	/// </summary>
	/// <param name="FellBackFrom">"NPU" or "GPU" when the work was meant for that device; null when it runs where it was meant to.</param>
	/// <param name="Why">One line: why it doesn't run on <paramref name="FellBackFrom"/>.</param>
	internal readonly record struct AcceleratorPlan(AiDevice Device, GpuAdapter? Card, string? FellBackFrom, string? Why) {
		/// <summary>The plan for a setting, from what this PC has and the failure markers now.</summary>
		public static AcceleratorPlan For(AiDevice setting) {
			switch (setting) {
				case AiDevice.Cpu:
					return new(AiDevice.Cpu, null, null, null);
				case AiDevice.Gpu: {
					// Asked for: tried whatever Heiward's markers say (a success clears them).
					GpuAdapter? card = GpuComponents.IsSupportedPlatform ? GpuAdapters.InUseNow() : null;
					return GpuComponents.IsInstalled
						? new(AiDevice.Gpu, card, null, null)
						: new(AiDevice.Cpu, null, "GPU", GpuComponents.IsSupportedPlatform ? "the GPU pack is not installed" : "this PC can't run the GPU pack");
				}
				case AiDevice.Npu:
					return NpuCandidate
						? new(AiDevice.Npu, null, null, null)
						: new(AiDevice.Cpu, null, "NPU", "no NPU this build can drive, or the NPU pack is not installed");
				default: {
					bool gpuPack = GpuComponents.IsInstalled && AiComponents.TestOverrideModelPath == null;
					GpuAdapter? card = gpuPack ? GpuAdapters.InUseNow() : null;
					return ChooseAuto(NpuCandidate, Accelerators.FailureOf(Accelerators.Npu), gpuPack, card,
						card != null && GpuChecks.Passed(card), card != null ? Accelerators.FailureOf(card.AcceleratorId) : null);
				}
			}
		}

		/// <summary>
		/// The NPU can be tried without loading anything: this build drives this PC's NPU, its pack is here, and so is ONNX
		/// Runtime. (Whether its plugin then finds the NPU is known only once ONNX Runtime is loaded.)
		/// </summary>
		static bool NpuCandidate =>
			AiComponents.TestOverrideModelPath == null && NpuComponents.IsSupportedPlatform && NpuComponents.IsInstalled && AiComponents.IsReady;

		/// <summary>
		/// Auto: the NPU when there is one that hasn't failed for Heiward in the last 10 minutes; else, with the GPU pack, the
		/// card in use once it has passed a check (<see cref="GpuChecks"/>) and hasn't failed either; else the CPU. The failures
		/// are Heiward's own markers only (<see cref="Accelerators.Folder"/>). Leaving the NPU, or a checked card, because it
		/// failed is a fallback, and says why: the failure Heiward met.
		/// </summary>
		internal static AcceleratorPlan ChooseAuto(bool npu, Accelerators.Failure? npuFailed, bool gpuPack, GpuAdapter? card, bool cardChecked, Accelerators.Failure? cardFailed) {
			if (npu && npuFailed == null)
				return new(AiDevice.Npu, null, null, null);
			string? npuWhy = npu ? Failed(npuFailed!) : null;
			if (gpuPack && card != null && cardChecked) {
				if (cardFailed == null)
					return new(AiDevice.Gpu, card, npuWhy != null ? "NPU" : null, npuWhy);
				string cardWhy = Failed(cardFailed);
				return npuWhy != null ? new(AiDevice.Cpu, null, "NPU", npuWhy + "; " + cardWhy) : new(AiDevice.Cpu, null, "GPU", cardWhy);
			}
			return new(AiDevice.Cpu, null, npuWhy != null ? "NPU" : null, npuWhy);
		}

		/// <summary>"the Qualcomm Hexagon NPU pack failed to load (…), at 15:18 (tried again from 15:28)": the failure as Heiward recorded it.</summary>
		static string Failed(Accelerators.Failure f) =>
			$"{f.Reason}, at {f.SinceUtc.ToLocalTime():HH:mm} (tried again from {f.UntilUtc.ToLocalTime():HH:mm})";
	}
}
