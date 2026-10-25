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

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace HEI.Agent.Tests;

/// <summary>
/// The self-update's signature check (Authenticode): a file Windows trusts, signed by a pinned certificate and timestamped,
/// passes; an unsigned, changed or someone else's file doesn't, and the self-update deletes it instead of installing it.
/// The signed file is this runtime's own System.Private.CoreLib.dll, Microsoft's, timestamped as a release is. Revocation
/// isn't checked here, so the tests need no network.
/// </summary>
public sealed class AuthenticodeTests : IDisposable {
	readonly string dir = Path.Combine(Path.GetTempPath(), "hei-authenticode-" + Guid.NewGuid().ToString("N"));

	public AuthenticodeTests() => Directory.CreateDirectory(dir);

	public void Dispose() {
		try { Directory.Delete(dir, true); } catch { }
	}

	/// <summary>A copy of a file Microsoft signed, and its signer's thumbprint.</summary>
	(string File, string Thumbprint) MicrosoftSigned() {
		string copy = Path.Combine(dir, "signed.dll");
		File.Copy(typeof(object).Assembly.Location, copy);
#pragma warning disable SYSLIB0057 // the signer of a signed file: what the check is compared with
		using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(copy));
#pragma warning restore SYSLIB0057
		return (copy, cert.Thumbprint);
	}

	/// <summary>A program no one signed: this test assembly.</summary>
	string Unsigned() {
		string copy = Path.Combine(dir, "unsigned.dll");
		File.Copy(typeof(AuthenticodeTests).Assembly.Location, copy);
		return copy;
	}

	[Fact]
	public void AFileSignedByAPinnedCertificate_AndTimestamped_Passes() {
		var (file, thumbprint) = MicrosoftSigned();
		Assert.Null(Authenticode.Problem(file, [thumbprint], checkRevocation: false));
		Assert.Null(Authenticode.Problem(file, [thumbprint.ToLowerInvariant()], checkRevocation: false));
	}

	[Fact]
	public void AFileSignedBySomeoneElse_DoesNot() {
		var (file, _) = MicrosoftSigned();
		string? problem = Authenticode.Problem(file, Authenticode.PublisherThumbprints, checkRevocation: false);
		Assert.NotNull(problem);
		Assert.Contains("not Heiward's publisher", problem);
	}

	[Fact]
	public void AnUnsignedFile_DoesNot() {
		Assert.Equal("it isn't signed", Authenticode.Problem(Unsigned(), Authenticode.PublisherThumbprints, checkRevocation: false));
		string text = Path.Combine(dir, "notes.txt");
		File.WriteAllText(text, "not a program");
		Assert.NotNull(Authenticode.Problem(text, Authenticode.PublisherThumbprints, checkRevocation: false));
	}

	[Fact]
	public void ASignedFileChangedAfterwards_DoesNot() {
		var (file, thumbprint) = MicrosoftSigned();
		byte[] bytes = File.ReadAllBytes(file);
		bytes[bytes.Length / 2] ^= 0xFF;
		File.WriteAllBytes(file, bytes);
		string? problem = Authenticode.Problem(file, [thumbprint], checkRevocation: false);
		Assert.NotNull(problem);
		Assert.Contains("doesn't trust", problem);
	}

	[Fact]
	public void ThePublishersCertificate_IsPinned() {
		Assert.Contains("6B654D9C34800D4307D42ACD63B925EDA5CE8D33", Authenticode.PublisherThumbprints);
		Assert.All(Authenticode.PublisherThumbprints, t => Assert.Matches("^[0-9A-F]{40}$", t));
	}

	[Fact]
	public void TheInstalledRelease_OnThisPc_Passes() {
		// A released hei.exe, where this PC has one installed: signed by the publisher's certificate as the check expects.
		string exe = Installer.InstalledExe;
		if (!File.Exists(exe)) return;
		string signer;
		try {
#pragma warning disable SYSLIB0057
			using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(exe));
#pragma warning restore SYSLIB0057
			signer = cert.Thumbprint;
		}
		catch (CryptographicException) {
			return; // a development build, installed unsigned
		}
		if (!Authenticode.PublisherThumbprints.Contains(signer)) return;
		Assert.Null(Authenticode.Problem(exe, Authenticode.PublisherThumbprints, checkRevocation: false));
	}

	sealed class StubHandler(byte[] body) : HttpMessageHandler {
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
			Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
	}

	static string Sum(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

	[Fact]
	public async Task TheSelfUpdate_DeletesAnUnsignedExe_ThatMatchesItsSum() {
		byte[] exe = File.ReadAllBytes(Unsigned());
		string folder = Path.Combine(dir, "updates");
		using var http = new HttpClient(new StubHandler(exe));

		var e = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			SelfUpdate.DownloadAsync(http, "http://localhost/Heiward-9.9.9-x64.exe", "Heiward-9.9.9-x64.exe", Sum(exe), folder, Authenticode.PublisherThumbprints, CancellationToken.None));

		Assert.Equal("Heiward-9.9.9-x64.exe wasn't installed: it isn't signed", e.Message);
		Assert.Empty(Directory.GetFiles(folder));
	}

	[Fact]
	public async Task TheSelfUpdate_DeletesAFile_ThatDoesNotMatchItsSum() {
		byte[] exe = File.ReadAllBytes(Unsigned());
		string folder = Path.Combine(dir, "updates");
		using var http = new HttpClient(new StubHandler(exe));

		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			SelfUpdate.DownloadAsync(http, "http://localhost/Heiward-9.9.9-x64.exe", "Heiward-9.9.9-x64.exe", new string('0', 64), folder, Authenticode.PublisherThumbprints, CancellationToken.None));

		Assert.Empty(Directory.GetFiles(folder));
	}
}
