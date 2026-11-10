// /*
//     Copyright (C) 2026 0x90d
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

using System.Reflection;
using System.Text.Json;
using HEI.Core.ViewModels;
using HEI.GUI.Data;
using HEI.GUI.ViewModels;

namespace HEI.GUI.HeadlessTests;

/// <summary>
/// Delete highlighted and the cleanup dry-run report work in one pass over the results instead
/// of once per file or group. Each test runs the one-at-a-time code these replaced next to the
/// new one, on the same made-up results, and expects the same list, counters, undo and report.
/// </summary>
public sealed class ResultsBulkRemovalTests {
	/// <summary>
	/// Made-up results, the same for the same seed: groups of 2 to 8 files whose members are
	/// spread through the list, about a third checked, a few with no size (gone from disk).
	/// </summary>
	static List<DuplicateItemVM> MadeUp(int seed, int count = 900) {
		var rnd = new Random(seed);
		var groups = new List<Guid>();
		var items = new List<DuplicateItemVM>(count);
		for (int i = 0; i < count; i++) {
			if (groups.Count == 0 || rnd.Next(5) == 0) {
				var bytes = new byte[16];
				rnd.NextBytes(bytes);
				groups.Add(new Guid(bytes));
			}
			// Mostly the newest group, sometimes an older one, so groups interleave.
			Guid group = rnd.Next(3) == 0 ? groups[rnd.Next(groups.Count)] : groups[^1];
			var item = new DuplicateItemVM(new DuplicateItem {
				Path = $@"Q:\made-up\{i:D5}.jpg",
				Folder = @"Q:\made-up",
				GroupId = group,
				SizeLong = rnd.Next(10) == 0 ? -1 : 1000 + rnd.Next(5_000_000),
				Similarity = 90f + rnd.Next(11),
				FrameSize = "1920x1080",
				FrameSizeInt = 1920 * 1080,
				DateCreated = new DateTime(2024, 1, 1).AddMinutes(i),
			});
			item.Checked = rnd.Next(3) == 0;
			items.Add(item);
		}
		return items;
	}

	static MainWindowVM Load(List<DuplicateItemVM> items) {
		var vm = new MainWindowVM();
		foreach (var item in items)
			vm.Duplicates.Add(item);
		vm.RebuildResultsList();
		return vm;
	}

	/// <summary>Delete highlighted as it was before: one Remove per highlighted row.</summary>
	static void DeleteHighlightedOneAtATime(MainWindowVM vm, List<DuplicateItemVM> selected) {
		if (selected.Count == 0) return;
		foreach (var item in selected)
			vm.Duplicates.Remove(item);
		typeof(MainWindowVM).GetMethod("RefreshGroupStats", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, null);
		vm.RefreshResultsView();
	}

	static void DeleteHighlighted(MainWindowVM vm, List<DuplicateItemVM> selected) {
		vm.NewResultsSelectionProvider = () => selected;
		vm.DeleteHighlightedCommand.Execute().Subscribe();
	}

	/// <summary>Everything the window shows about the results, item by item and row by row.</summary>
	static string Snapshot(MainWindowVM vm) {
		var lines = new List<string> {
			$"files {vm.Duplicates.Count}, total {vm.TotalDuplicates}, groups {vm.TotalDuplicateGroups}",
			$"size {vm.TotalDuplicatesSize}, savings {vm.PotentialSavings}",
			$"checked {vm.DuplicatesCheckedCounter}, checked size {vm.DuplicatesCheckedSize}",
			$"summary {vm.CheckedSummaryText}",
		};
		lines.AddRange(vm.Duplicates.Select(d => $"{d.ItemInfo.Path} {d.ItemInfo.GroupId} {(d.Checked ? "[x]" : "[ ]")}"));
		lines.AddRange(vm.ResultsRows.Select(row => row switch {
			ResultsGroupHeader h => $"group {h.GroupNumber} {h.GroupId} files {h.FileCount} bytes {h.TotalBytes} wasted {h.WastedBytes}",
			ResultsItemRow r => $"  row {r.Item.ItemInfo.Path} best {r.IsBest}",
			_ => "  " + row.GetType().Name,
		}));
		return string.Join("\n", lines);
	}

	/// <summary>The same rows highlighted in two copies of the results (copies hold their own objects).</summary>
	static List<DuplicateItemVM> Pick(List<DuplicateItemVM> items, IEnumerable<int> indexes) =>
		indexes.Select(i => i < 0 ? null! : items[i]).ToList();

	static List<int> Highlighted(int seed, int count, double share) {
		var rnd = new Random(seed * 7919);
		var picked = Enumerable.Range(0, count).Where(_ => rnd.NextDouble() < share).ToList();
		// In the order the rows were clicked, not list order.
		return picked.OrderBy(_ => rnd.Next()).ToList();
	}

	[Theory]
	[InlineData(1, 0.2)]
	[InlineData(2, 0.6)]
	[InlineData(3, 1.0)]
	public Task DeleteHighlighted_LeavesWhatRemovingOneAtATimeLeft(int seed, double share) => HeadlessUi.Run(() => {
		var before = MadeUp(seed);
		var after = MadeUp(seed);
		var oldVm = Load(before);
		var newVm = Load(after);
		var rows = Highlighted(seed, before.Count, share);

		var oldSelected = Pick(before, rows);
		var newSelected = Pick(after, rows);
		// A row clicked twice, an empty slot and a file already gone from the list are ignored.
		if (rows.Count > 0) {
			oldSelected.Add(before[rows[0]]);
			newSelected.Add(after[rows[0]]);
		}
		oldSelected.Insert(oldSelected.Count / 2, null!);
		newSelected.Insert(newSelected.Count / 2, null!);
		oldSelected.Add(new DuplicateItemVM(new DuplicateItem { Path = @"Q:\gone.jpg", GroupId = before[0].ItemInfo.GroupId }));
		newSelected.Add(new DuplicateItemVM(new DuplicateItem { Path = @"Q:\gone.jpg", GroupId = after[0].ItemInfo.GroupId }));

		DeleteHighlightedOneAtATime(oldVm, oldSelected);
		DeleteHighlighted(newVm, newSelected);

		Assert.Equal(Snapshot(oldVm), Snapshot(newVm));
		Assert.Equal(before.Count - rows.Count, newVm.Duplicates.Count);
		Assert.Equal(before.Count(d => d.Checked && oldVm.Duplicates.Contains(d)), newVm.DuplicatesCheckedCounter);

		// What was taken out no longer counts when it changes; what stayed still does.
		var gone = rows.Select(i => after[i]).FirstOrDefault();
		if (gone != null) {
			int checkedBefore = newVm.DuplicatesCheckedCounter;
			gone.Checked = !gone.Checked;
			Assert.Equal(checkedBefore, newVm.DuplicatesCheckedCounter);
		}
		var stayed = Enumerable.Range(0, after.Count).Except(rows).Select(i => (Old: before[i], New: after[i])).FirstOrDefault();
		if (stayed.New != null) {
			stayed.Old.Checked = !stayed.Old.Checked;
			stayed.New.Checked = !stayed.New.Checked;
			Assert.Equal(oldVm.DuplicatesCheckedCounter, newVm.DuplicatesCheckedCounter);
			Assert.Equal(oldVm.CheckedSummaryText, newVm.CheckedSummaryText);
		}
	});

	[Fact]
	public Task DeleteHighlighted_KeepsTheSelectionUndo() => HeadlessUi.Run(() => {
		var before = MadeUp(4);
		var after = MadeUp(4);
		var oldVm = Load(before);
		var newVm = Load(after);
		// One gesture checks every fourth file; then a third of those, and others, are deleted.
		foreach (var (vm, items) in new[] { (oldVm, before), (newVm, after) }) {
			using var batch = vm.BeginSelectionUndoBatch();
			for (int i = 0; i < items.Count; i += 4)
				items[i].Checked = true;
		}
		var rows = Highlighted(4, before.Count, 0.3);
		DeleteHighlightedOneAtATime(oldVm, Pick(before, rows));
		DeleteHighlighted(newVm, Pick(after, rows));
		Assert.Equal(Snapshot(oldVm), Snapshot(newVm));

		oldVm.UndoSelectionCommand.Execute().Subscribe();
		newVm.UndoSelectionCommand.Execute().Subscribe();
		Assert.Equal(Snapshot(oldVm), Snapshot(newVm));
		// The undo step was still there: a file the gesture checked that stayed is unchecked again.
		int stayedChecked = Enumerable.Range(0, after.Count).Where(i => i % 4 == 0).Except(rows)
			.First(i => !MadeUp(4)[i].Checked);
		Assert.False(after[stayedChecked].Checked);
	});

	[Fact]
	public Task DeleteHighlighted_TheSameFileListedTwice_GoesAsRemoveDid() => HeadlessUi.Run(() => {
		foreach (int times in new[] { 1, 2 }) {
			var before = MadeUp(5, 40);
			var after = MadeUp(5, 40);
			var oldVm = Load(before);
			var newVm = Load(after);
			// The same object a second time, further down the list.
			oldVm.Duplicates.Add(before[3]);
			newVm.Duplicates.Add(after[3]);
			before[3].Checked = after[3].Checked = true;

			DeleteHighlightedOneAtATime(oldVm, Enumerable.Repeat(before[3], times).Append(before[10]).ToList());
			DeleteHighlighted(newVm, Enumerable.Repeat(after[3], times).Append(after[10]).ToList());
			Assert.Equal(Snapshot(oldVm), Snapshot(newVm));
			Assert.Equal(2 - times, newVm.Duplicates.Count(d => ReferenceEquals(d, after[3])));

			before[3].Checked = after[3].Checked = false;
			Assert.Equal(Snapshot(oldVm), Snapshot(newVm));
		}
	});

	/// <summary>The cleanup dry-run report as it was built before: the whole list searched per group.</summary>
	static CleanupDryRunReport ReportOneGroupAtATime(MainWindowVM vm, IReadOnlyList<DuplicateItemVM> toDelete) {
		var itemsByGroup = toDelete.GroupBy(x => x.ItemInfo.GroupId).ToList();
		var groups = new List<CleanupDryRunGroup>();

		foreach (var group in itemsByGroup) {
			var allGroupItems = vm.Duplicates.Where(d => d.ItemInfo.GroupId == group.Key).ToList();
			var keepItems = allGroupItems.Except(group).ToList();
			long savings = group.Sum(item => item.ItemInfo.SizeLong);
			groups.Add(new CleanupDryRunGroup {
				GroupId = group.Key,
				EstimatedSavingsBytes = savings,
				Reason = App.Lang["CleanupDryRun.Reason.Manual"],
				RemoveItems = group.Select(item => new CleanupDryRunItem {
					Path = item.ItemInfo.Path,
					SizeBytes = item.ItemInfo.SizeLong,
					Resolution = item.ItemInfo.FrameSize ?? string.Empty,
					DateCreated = item.ItemInfo.DateCreated
				}).ToList(),
				KeepItems = keepItems.Select(item => new CleanupDryRunItem {
					Path = item.ItemInfo.Path,
					SizeBytes = item.ItemInfo.SizeLong,
					Resolution = item.ItemInfo.FrameSize ?? string.Empty,
					DateCreated = item.ItemInfo.DateCreated
				}).ToList()
			});
		}

		return new CleanupDryRunReport {
			CreatedAt = DateTime.UtcNow,
			EstimatedTotalSavingsBytes = groups.Sum(g => g.EstimatedSavingsBytes),
			Groups = groups
		};
	}

	/// <summary>The report as the file it is saved to, with the time it was made set aside.</summary>
	static string Saved(CleanupDryRunReport report) {
		report.CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
		return JsonSerializer.Serialize(report, GuiJsonPrettyContext.Default.CleanupDryRunReport);
	}

	[Theory]
	[InlineData(6, 0.0)]
	[InlineData(7, 0.3)]
	[InlineData(8, 0.9)]
	public Task CleanupDryRunReport_SameAsSearchingPerGroup(int seed, double share) => HeadlessUi.Run(() => {
		var items = MadeUp(seed);
		var vm = Load(items);
		// The same object twice in the list; a group's keep list names it once, as Except did.
		vm.Duplicates.Add(items[1]);
		var rnd = new Random(seed);
		var toDelete = items.Where(_ => rnd.NextDouble() < share).OrderBy(_ => rnd.Next()).ToList();
		if (toDelete.Count > 0)
			toDelete.Add(toDelete[0]); // listed twice
		// Files no longer in the list: one of a group that still is, one of a group that is not.
		toDelete.Add(new DuplicateItemVM(new DuplicateItem { Path = @"Q:\gone-1.jpg", GroupId = items[2].ItemInfo.GroupId, SizeLong = 5 }));
		toDelete.Add(new DuplicateItemVM(new DuplicateItem { Path = @"Q:\gone-2.jpg", GroupId = Guid.NewGuid(), SizeLong = 7 }));

		string expected = Saved(ReportOneGroupAtATime(vm, toDelete));
		string actual = Saved(vm.BuildCleanupDryRunReport(toDelete));
		Assert.Equal(expected, actual);
		Assert.Contains("\"KeepItems\"", actual);

		// A report of a few files out of many groups, too.
		var few = toDelete.Skip(toDelete.Count - 3).ToList();
		Assert.Equal(Saved(ReportOneGroupAtATime(vm, few)), Saved(vm.BuildCleanupDryRunReport(few)));
	});
}
