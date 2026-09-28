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

using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using HEI.Core.AI;
using HEI.Core.Utils;

namespace HEI.Agent {
	/// <summary>
	/// Prerequisites another copy already downloaded, copied instead of downloaded again. A copy of Heiward
	/// or Video Duplicate Finder keeps them in its own folder: FFmpeg in bin\, ONNX Runtime and the int8
	/// model in ai\, the NPU pack in ai\qnn\ and the GPU pack in ai\gpu\ (both with the fp32 model in ai\).
	/// A part is copied only from a folder that has all of it, and kept only when it passes, in its new
	/// place, the check the caller gives: the one a download has to pass, plus a probe run for the NPU and
	/// GPU packs. Otherwise the copied files are removed again and the caller downloads as before.
	/// </summary>
	static class ComponentReuse {
		/// <summary>One prerequisite: whether a folder has all of it, and its files there (relative paths).</summary>
		internal sealed record Part(string Name, Func<string, bool> Complete, Func<string, IEnumerable<string>> Files);

		const string RuntimeMarker = "runtime.version"; // AiComponents writes the ONNX Runtime version here

		/// <summary>FFmpeg: ffmpeg.exe, ffprobe.exe and the shared libraries the in-process decoder loads.</summary>
		internal static readonly Part Ffmpeg = new("FFmpeg",
			root => File.Exists(Path.Combine(root, "bin", "ffmpeg.exe")) && File.Exists(Path.Combine(root, "bin", "ffprobe.exe")) &&
				ffmpeg.LibraryVersionMap.All(l => File.Exists(Path.Combine(root, "bin", $"{l.Key}-{l.Value}.dll"))) &&
				ForThisProcess(Path.Combine(root, "bin", $"avcodec-{ffmpeg.LibraryVersionMap["avcodec"]}.dll")),
			root => FilesUnder(root, "bin", recurse: false));

		/// <summary>ONNX Runtime (the version this build expects) and the int8 model, directly in ai\.</summary>
		internal static readonly Part AiRuntime = new("the AI components",
			root => File.Exists(Path.Combine(root, "ai", AiComponents.ModelFileName)) && File.Exists(Path.Combine(root, "ai", "onnxruntime.dll")) &&
				ReadMarker(Path.Combine(root, "ai", RuntimeMarker)) == AiComponents.RuntimeVersion && ForThisProcess(Path.Combine(root, "ai", "onnxruntime.dll")),
			root => FilesUnder(root, "ai", recurse: false).Where(f => {
				string name = Path.GetFileName(f);
				return name.StartsWith("onnxruntime", StringComparison.OrdinalIgnoreCase) || name == RuntimeMarker || name == AiComponents.ModelFileName;
			}));

		internal static Part NpuPack => NpuPackFor(NpuComponents.PackFolders, NpuComponents.PackKeyFile);

		/// <summary>
		/// A vendor's NPU pack: its folders under ai\ (the plugin, and the graphs it already compiled, so the
		/// first scan skips that) and the fp32 model; complete when the pack's key file is there.
		/// </summary>
		internal static Part NpuPackFor(IReadOnlyList<string> folders, string? keyFile) => new("the NPU pack",
			root => keyFile != null && File.Exists(Path.Combine(root, "ai", NpuComponents.ModelFileName)) && ForThisProcess(Path.Combine(root, "ai", keyFile)),
			root => folders.SelectMany(f => FilesUnder(root, Path.Combine("ai", f), recurse: true))
				.Append(Path.Combine("ai", NpuComponents.ModelFileName)));

		/// <summary>The GPU pack (ONNX Runtime DirectML and DirectML) and the fp32 model.</summary>
		internal static readonly Part GpuPack = new("the GPU pack",
			root => File.Exists(Path.Combine(root, "ai", NpuComponents.ModelFileName)) &&
				new[] { "onnxruntime.dll", "onnxruntime_providers_shared.dll", "DirectML.dll" }.All(f => File.Exists(Path.Combine(root, "ai", "gpu", f))) &&
				ForThisProcess(Path.Combine(root, "ai", "gpu", "onnxruntime.dll")),
			root => FilesUnder(root, Path.Combine("ai", "gpu"), recurse: true).Append(Path.Combine("ai", NpuComponents.ModelFileName)));

		/// <summary>
		/// Where to look, in order: the folders given (--reuse-from, and the folder the installer was started
		/// from), then Video Duplicate Finder's per-user folder. Never <paramref name="target"/> itself.
		/// </summary>
		public static List<string> Sources(IEnumerable<string>? given, string target) {
			string own = Full(target);
			return (given ?? Enumerable.Empty<string>()).Append(CoreUtils.GetDefaultStateFolder())
				.Where(d => !string.IsNullOrWhiteSpace(d))
				.Select(Full)
				.Where(d => Directory.Exists(d) && !d.Equals(own, StringComparison.OrdinalIgnoreCase))
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToList();
		}

		/// <summary>The first source that has all of <paramref name="part"/>, without copying anything.</summary>
		public static string? Find(Part part, IEnumerable<string> sources) => sources.FirstOrDefault(part.Complete);

		/// <summary>
		/// Copies <paramref name="part"/> into <paramref name="target"/> from the first source that has it and
		/// whose copy passes <paramref name="works"/>. Returns that source, or null with nothing left behind.
		/// </summary>
		public static async Task<string?> TryCopyAsync(Part part, IEnumerable<string> sources, string target, Func<Task<bool>> works) {
			foreach (string source in sources.Where(part.Complete)) {
				var copied = new List<string>();
				try {
					foreach (string rel in part.Files(source).Distinct(StringComparer.OrdinalIgnoreCase)) {
						string to = Path.Combine(target, rel);
						Directory.CreateDirectory(Path.GetDirectoryName(to)!);
						File.Copy(Path.Combine(source, rel), to, overwrite: true);
						copied.Add(to);
					}
					if (await works()) return source;
				}
				catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
					AgentPaths.AppendLog($"reusing {part.Name} from {source} failed: {e.Message}");
				}
				foreach (string f in copied)
					try { File.Delete(f); } catch { /* a leftover is replaced by the download */ }
			}
			return null;
		}

		/// <summary>
		/// True when a DLL is built for this process's processor (its PE header's machine field). A version marker
		/// can't tell: an Arm64 copy can't load a folder of x64 libraries, nor the reverse. Conservative: a
		/// hybrid (ARM64X) library reads as Arm64, so an x64 process downloads its own instead.
		/// </summary>
		internal static bool ForThisProcess(string path) {
			try {
				using FileStream f = File.OpenRead(path);
				using var reader = new BinaryReader(f);
				if (reader.ReadUInt16() != 0x5A4D) return false; // "MZ"
				f.Position = 0x3C;
				f.Position = reader.ReadInt32();
				if (reader.ReadUInt32() != 0x00004550) return false; // "PE\0\0"
				ushort machine = reader.ReadUInt16();
				return RuntimeInformation.ProcessArchitecture switch {
					Architecture.X64 => machine == 0x8664,
					Architecture.Arm64 => machine == 0xAA64,
					_ => false,
				};
			}
			catch { return false; }
		}

		/// <summary>Relative paths of the files in root\sub, never following a link out of it.</summary>
		static IEnumerable<string> FilesUnder(string root, string sub, bool recurse) {
			string dir = Path.Combine(root, sub);
			if (!Directory.Exists(dir)) return Enumerable.Empty<string>();
			var options = new EnumerationOptions { RecurseSubdirectories = recurse, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
			return Directory.EnumerateFiles(dir, "*", options).Select(f => Path.GetRelativePath(root, f)).ToList();
		}

		static string? ReadMarker(string path) {
			try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
			catch { return null; }
		}

		static string Full(string d) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(d));
	}
}
