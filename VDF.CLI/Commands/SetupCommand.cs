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

using System.CommandLine;
using VDF.Core.AI;
using VDF.Core.FFTools;

namespace VDF.CLI.Commands {
	/// <summary>
	/// <c>vdf-cli setup</c>: installs what a headless scan needs (FFmpeg, and optionally the AI
	/// components and the NPU pack) and reports what this machine will use. The CLI downloads the
	/// AI parts on demand anyway, but FFmpeg it never did — a scheduled scan has no one to ask.
	/// </summary>
	internal static class SetupCommand {
		static readonly Option<bool> Ai = new("--ai") {
			Description = "Also install the AI components, and on Windows ARM64 the NPU pack."
		};

		internal static Command Build() {
			var cmd = new Command("setup", "Install FFmpeg (and with --ai the AI components) for headless scans, then report the AI device.");
			cmd.Options.Add(Ai);
			cmd.SetAction(async (parseResult, ct) => {
				try {
					if (FFToolsUtils.GetPath(FFToolsUtils.FFTool.FFmpeg) == null || FFToolsUtils.GetPath(FFToolsUtils.FFTool.FFProbe) == null) {
						Console.Error.WriteLine("[setup] Downloading FFmpeg...");
						string folder = await FfmpegDownloader.DownloadAndInstallAsync(null, ct);
						Console.Error.WriteLine($"[setup] FFmpeg installed to '{folder}'.");
					}
					else
						Console.Error.WriteLine("[setup] FFmpeg found.");
					if (parseResult.GetValue(Ai)) {
						var settings = new VDF.Core.Settings { UseAiMatching = true };
						await ScanRunner.EnsureAiComponentsAsync(settings, ct);
						using var embedder = OnnxEmbedder.Create(AiDevice.Auto);
						Console.Error.WriteLine($"[setup] AI embeddings will run on the {embedder.DeviceName}.");
					}
					return 0;
				}
				catch (OperationCanceledException) {
					return 130;
				}
				catch (Exception e) {
					Console.Error.WriteLine($"[setup] Failed: {e.Message}");
					return 1;
				}
			});
			return cmd;
		}
	}
}
