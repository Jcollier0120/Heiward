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

using HEI.Core.AI;

namespace HEI.Core.Tests.AI;

/// <summary>The Vitis AI plugin loaded through a junction in {ai}, as its compiler needs (<see cref="PluginLink"/>).</summary>
public class PluginLinkTests : IDisposable {
	readonly string root = Path.Combine(Path.GetTempPath(), "heiward-link-" + Guid.NewGuid().ToString("N"));

	public PluginLinkTests() => Directory.CreateDirectory(root);

	public void Dispose() {
		// .NET's first pass removes a junction and then throws (see below): a second deletes the rest.
		for (int i = 0; i < 2 && Directory.Exists(root); i++)
			try { Directory.Delete(root, recursive: true); } catch { }
	}

	/// <summary>A plugin's folder as Windows ML keeps one: its DLL and the DLLs beside it.</summary>
	string Plugin(string version) {
		string folder = Path.Combine(root, $"Plugin_{version}_x64", "ExecutionProvider");
		Directory.CreateDirectory(folder);
		File.WriteAllText(Path.Combine(folder, "onnxruntime_vitisai_ep.dll"), version);
		File.WriteAllText(Path.Combine(folder, "dyn_dispatch_core.dll"), version);
		return Path.Combine(folder, "onnxruntime_vitisai_ep.dll");
	}

	[Fact]
	public void The_plugin_is_loaded_through_a_junction_to_its_folder() {
		if (!OperatingSystem.IsWindows()) return;
		string plugin = Plugin("1.8.75.0"), link = Path.Combine(root, "ai", "vitisai-plugin");
		Directory.CreateDirectory(Path.GetDirectoryName(link)!);

		string through = PluginLink.Through(plugin, link);

		Assert.Equal(Path.Combine(link, "onnxruntime_vitisai_ep.dll"), through);
		Assert.True(new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint));
		Assert.True(PluginLink.PointsTo(link, Path.GetDirectoryName(plugin)!));
		Assert.Equal("1.8.75.0", File.ReadAllText(through));
		Assert.Equal("1.8.75.0", File.ReadAllText(Path.Combine(link, "dyn_dispatch_core.dll"))); // the DLLs beside it too
	}

	[Fact]
	public void A_junction_already_there_is_kept_and_one_to_an_old_plugin_is_re_pointed() {
		if (!OperatingSystem.IsWindows()) return;
		string old = Plugin("1.8.72.0"), updated = Plugin("1.8.75.0"), link = Path.Combine(root, "vitisai-plugin");
		PluginLink.Through(old, link);
		DateTime made = Directory.GetCreationTimeUtc(link);

		Assert.Equal(Path.Combine(link, "onnxruntime_vitisai_ep.dll"), PluginLink.Through(old, link));
		Assert.Equal(made, Directory.GetCreationTimeUtc(link));

		// Windows updated the plugin into a new folder.
		string through = PluginLink.Through(updated, link);
		Assert.Equal("1.8.75.0", File.ReadAllText(through));
		Assert.True(File.Exists(old)); // the old plugin's files are Windows' to remove, not ours
	}

	[Fact]
	public void A_folder_that_can_t_become_a_junction_leaves_the_plugin_in_its_own_folder() {
		if (!OperatingSystem.IsWindows()) return;
		string plugin = Plugin("1.8.75.0"), link = Path.Combine(root, "vitisai-plugin");
		Directory.CreateDirectory(link);
		File.WriteAllText(Path.Combine(link, "someone's.txt"), "kept");

		Assert.Equal(plugin, PluginLink.Through(plugin, link));
		Assert.Equal("kept", File.ReadAllText(Path.Combine(link, "someone's.txt")));
	}

	[Fact]
	public void The_uninstall_s_delete_removes_the_junction_and_never_the_plugin_s_files() {
		if (!OperatingSystem.IsWindows()) return;
		string plugin = Plugin("1.8.75.0"), install = Path.Combine(root, "Heiward");
		PluginLink.Through(plugin, Path.Combine(install, "ai", "vitisai-plugin"));

		// Uninstalling deletes the install folder with cmd's rmdir (Installer.DeleteFolderLater), which unlinks a junction.
		using (var rmdir = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/d /c rmdir /s /q \"{install}\"") { CreateNoWindow = true, UseShellExecute = false })!)
			rmdir.WaitForExit();

		Assert.False(Directory.Exists(install));
		Assert.True(File.Exists(plugin));
	}

	[Fact]
	public void Dot_NET_s_delete_takes_a_second_pass_and_never_the_plugin_s_files() {
		if (!OperatingSystem.IsWindows()) return;
		string plugin = Plugin("1.8.75.0"), install = Path.Combine(root, "Heiward");
		PluginLink.Through(plugin, Path.Combine(install, "ai", "vitisai-plugin"));

		// .NET unmounts a junction with DeleteVolumeMountPoint first, which fails for a junction to a folder (access denied for
		// a standard user, another error for an administrator, as on CI): it still removes the junction, then throws. The GitHub
		// copy's removal (Installer.RemoveGitHubCopy) catches both and tries again, and the second pass deletes the rest.
		try { Directory.Delete(install, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
		Assert.True(File.Exists(plugin));
		if (Directory.Exists(install)) Directory.Delete(install, recursive: true);

		Assert.False(Directory.Exists(install));
		Assert.True(File.Exists(plugin));
	}
}
