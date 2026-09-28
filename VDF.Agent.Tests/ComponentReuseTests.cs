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

using System.Diagnostics;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using VDF.Core.AI;

namespace VDF.Agent.Tests;

/// <summary>The installer copies prerequisites another copy already has, and leaves nothing behind when a copy fails its check.</summary>
public sealed class ComponentReuseTests : IDisposable {
	readonly string root = Path.Combine(Path.GetTempPath(), "heiward-reuse-" + Guid.NewGuid().ToString("N"));

	public ComponentReuseTests() => Directory.CreateDirectory(root);

	public void Dispose() {
		foreach (string d in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
			if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) Directory.Delete(d);
		try { Directory.Delete(root, true); } catch { }
	}

	string Dir(string name) {
		string p = Path.Combine(root, name);
		Directory.CreateDirectory(p);
		return p;
	}

	static void Touch(string path, string text = "x") {
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, text);
	}

	/// <summary>A stand-in DLL: just the headers that say which processor it's for.</summary>
	static void Library(string path, bool thisProcess = true) {
		ushort machine = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? (ushort)0xAA64 : (ushort)0x8664;
		if (!thisProcess) machine = machine == 0xAA64 ? (ushort)0x8664 : (ushort)0xAA64;
		var bytes = new byte[0x100];
		bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
		BitConverter.GetBytes(0x80).CopyTo(bytes, 0x3C);
		bytes[0x80] = (byte)'P'; bytes[0x81] = (byte)'E';
		BitConverter.GetBytes(machine).CopyTo(bytes, 0x84);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllBytes(path, bytes);
	}

	/// <summary>A folder laid out like an app's: FFmpeg in bin\, every library the in-process decoder loads.</summary>
	string FfmpegCopy(string name, bool withLibraries = true, bool thisProcess = true) {
		string d = Dir(name);
		Touch(Path.Combine(d, "bin", "ffmpeg.exe"));
		Touch(Path.Combine(d, "bin", "ffprobe.exe"));
		if (withLibraries)
			foreach (var l in ffmpeg.LibraryVersionMap) Library(Path.Combine(d, "bin", $"{l.Key}-{l.Value}.dll"), thisProcess);
		return d;
	}

	[Fact]
	public async Task A_complete_copy_is_copied_and_its_source_returned() {
		string source = FfmpegCopy("source"), target = Dir("target");
		string? from = await ComponentReuse.TryCopyAsync(ComponentReuse.Ffmpeg, new[] { source }, target, () => Task.FromResult(true));
		Assert.Equal(source, from);
		Assert.True(File.Exists(Path.Combine(target, "bin", "ffmpeg.exe")));
		Assert.True(ComponentReuse.Ffmpeg.Complete(target));
	}

	[Fact]
	public async Task A_copy_that_fails_its_check_is_removed_and_the_next_source_tried() {
		string broken = FfmpegCopy("broken"), good = FfmpegCopy("good"), target = Dir("target");
		Touch(Path.Combine(good, "bin", "marker.txt"));
		int calls = 0;
		// The first copy "crashes on load"; the second starts.
		string? from = await ComponentReuse.TryCopyAsync(ComponentReuse.Ffmpeg, new[] { broken, good }, target,
			() => Task.FromResult(++calls == 2));
		Assert.Equal(good, from);
		Assert.True(File.Exists(Path.Combine(target, "bin", "marker.txt")));

		string target2 = Dir("target2");
		Assert.Null(await ComponentReuse.TryCopyAsync(ComponentReuse.Ffmpeg, new[] { broken }, target2, () => Task.FromResult(false)));
		Assert.Empty(Directory.EnumerateFiles(target2, "*", SearchOption.AllDirectories));
	}

	[Fact]
	public void A_copy_for_another_processor_is_never_used() {
		// An Arm64 Heiward can't load x64 FFmpeg (or the reverse), whatever its version.
		string other = FfmpegCopy("other-cpu", thisProcess: false);
		Assert.False(ComponentReuse.Ffmpeg.Complete(other));
		Assert.True(ComponentReuse.ForThisProcess(Environment.ProcessPath!));
		Assert.False(ComponentReuse.ForThisProcess(Path.Combine(other, "bin", "ffmpeg.exe"))); // not a PE file at all
	}

	[Fact]
	public void An_incomplete_copy_is_never_used() {
		string noLibraries = FfmpegCopy("cli-only", withLibraries: false);
		Assert.False(ComponentReuse.Ffmpeg.Complete(noLibraries));
		Assert.Null(ComponentReuse.Find(ComponentReuse.Ffmpeg, new[] { noLibraries }));
	}

	[Fact]
	public void The_AI_runtime_must_be_the_version_this_build_expects() {
		string d = Dir("ai-copy");
		Touch(Path.Combine(d, "ai", AiComponents.ModelFileName));
		Library(Path.Combine(d, "ai", "onnxruntime.dll"));
		Touch(Path.Combine(d, "ai", "runtime.version"), "0.0.1");
		Assert.False(ComponentReuse.AiRuntime.Complete(d));
		Touch(Path.Combine(d, "ai", "runtime.version"), AiComponents.RuntimeVersion + "\n");
		Assert.True(ComponentReuse.AiRuntime.Complete(d));

		// Only the runtime and the int8 model: not the NPU or GPU packs, nor the fp32 model beside them.
		Touch(Path.Combine(d, "ai", NpuComponents.ModelFileName));
		Touch(Path.Combine(d, "ai", "qnn", "QnnHtp.dll"));
		var files = ComponentReuse.AiRuntime.Files(d).Select(Path.GetFileName).ToList();
		Assert.Contains("onnxruntime.dll", files);
		Assert.Contains("runtime.version", files);
		Assert.DoesNotContain(NpuComponents.ModelFileName, files);
		Assert.DoesNotContain("QnnHtp.dll", files);
	}

	/// <summary>The Qualcomm pack's layout (a test PC may have another vendor's NPU, or none).</summary>
	static readonly ComponentReuse.Part QnnLayout = ComponentReuse.NpuPackFor(
		new[] { "qnn", $"qnn-cache-{NpuComponents.QnnPackageVersion}" }, Path.Combine("qnn", "onnxruntime_providers_qnn.dll"));

	[Fact]
	public void The_NPU_pack_brings_its_compiled_graphs() {
		string d = Dir("npu-copy");
		Touch(Path.Combine(d, "ai", NpuComponents.ModelFileName));
		Library(Path.Combine(d, "ai", "qnn", "onnxruntime_providers_qnn.dll"));
		Touch(Path.Combine(d, "ai", "qnn", "QnnHtp.dll"));
		Touch(Path.Combine(d, "ai", $"qnn-cache-{NpuComponents.QnnPackageVersion}", "graph.bin"));
		Touch(Path.Combine(d, "ai", "qnn-cache-0.1", "stale.bin"));
		Assert.True(QnnLayout.Complete(d));
		var files = QnnLayout.Files(d).ToList();
		Assert.Contains(Path.Combine("ai", $"qnn-cache-{NpuComponents.QnnPackageVersion}", "graph.bin"), files);
		Assert.DoesNotContain(files, f => f.Contains("qnn-cache-0.1"));
	}

	[Fact]
	public void Sources_skip_the_target_missing_folders_and_repeats() {
		string a = Dir("a"), target = Dir("target");
		var sources = ComponentReuse.Sources(new[] { a, a + "\\", Path.Combine(root, "missing"), target }, target);
		Assert.Equal(a, sources[0]);
		Assert.Single(sources, s => s.Equals(a, StringComparison.OrdinalIgnoreCase));
		Assert.DoesNotContain(sources, s => s.Equals(target, StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public void A_linked_folder_inside_a_pack_is_not_followed() {
		string source = Dir("source"), elsewhere = Dir("elsewhere");
		Touch(Path.Combine(source, "ai", "qnn", "QnnHtp.dll"));
		Touch(Path.Combine(elsewhere, "secret.txt"));
		using (var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{Path.Combine(source, "ai", "qnn", "linked")}\" \"{elsewhere}\"") {
			CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true,
		})!) p.WaitForExit();
		Assert.True(Directory.Exists(Path.Combine(source, "ai", "qnn", "linked")));
		var files = QnnLayout.Files(source).ToList();
		Assert.Contains(Path.Combine("ai", "qnn", "QnnHtp.dll"), files);
		Assert.DoesNotContain(files, f => f.EndsWith("secret.txt"));
	}
}
