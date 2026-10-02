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

using HEI.Core.Utils;

namespace HEI.Core.Tests;

/// <summary>The graphics cards a desktop with more than one lists, and the one settings.json names.</summary>
public class GpuAdaptersTests {
	const ulong GB = 1UL << 30;

	/// <summary>A desktop as DXGI lists it: the card driving the display first, then the processor's graphics, then Windows' own.</summary>
	static readonly DxgiAdapter[] Desktop = {
		new(0, "NVIDIA GeForce RTX 4070", 12 * GB, 0x10DE, false),
		new(1, "Intel(R) UHD Graphics 770", 128UL << 20, 0x8086, false),
		new(2, "Microsoft Basic Render Driver", 0, 0x1414, true),
	};

	[Fact]
	public void TheHardwareCards_AreListed_WindowsOwnAdaptersAreNot() {
		IReadOnlyList<GpuAdapter> cards = GpuAdapters.Keyed(Desktop);
		Assert.Equal(new[] { "NVIDIA GeForce RTX 4070", "Intel(R) UHD Graphics 770" }, cards.Select(c => c.Key));
		Assert.Equal(new[] { 0, 1 }, cards.Select(c => c.Index));
	}

	[Fact]
	public void TheRemoteDisplayAdapter_IsLeftOut_ThoughNotFlaggedSoftware() =>
		Assert.Empty(GpuAdapters.Keyed(new DxgiAdapter[] { new(0, "Microsoft Remote Display Adapter", 0, 0x1414, false) }));

	[Fact]
	public void WithSoftwareAsked_WindowsOwnAdapter_IsListedToo() =>
		Assert.Equal(3, GpuAdapters.Keyed(Desktop, software: true).Count);

	[Fact]
	public void TwoCardsOfTheSameModel_AreTold_ApartByNumber() {
		IReadOnlyList<GpuAdapter> cards = GpuAdapters.Keyed(new DxgiAdapter[] {
			new(0, "AMD Radeon RX 7900 XTX", 24 * GB, 0x1002, false),
			new(1, "AMD Radeon RX 7900 XTX", 24 * GB, 0x1002, false),
			new(2, "AMD Radeon RX 7900 XTX ", 24 * GB, 0x1002, false), // a driver's trailing space
		});
		Assert.Equal(new[] { "AMD Radeon RX 7900 XTX", "AMD Radeon RX 7900 XTX #2", "AMD Radeon RX 7900 XTX #3" }, cards.Select(c => c.Key));
		Assert.All(cards, c => Assert.Equal("AMD Radeon RX 7900 XTX", c.Name));
	}

	[Fact]
	public void ACardWithoutAName_GetsOne() =>
		Assert.Equal("Graphics card 2", GpuAdapters.Keyed(new DxgiAdapter[] { new(1, "", GB, 0x10DE, false) })[0].Key);

	[Fact]
	public void TheSuggestedCard_HasTheMostMemoryOfItsOwn() {
		// The graphics card second in DXGI's list (a laptop's display runs on the processor's graphics).
		IReadOnlyList<GpuAdapter> cards = GpuAdapters.Keyed(new DxgiAdapter[] {
			new(0, "Intel(R) Arc(TM) Graphics", 128UL << 20, 0x8086, false),
			new(1, "NVIDIA GeForce RTX 4060 Laptop GPU", 8 * GB, 0x10DE, false),
		});
		Assert.Equal("NVIDIA GeForce RTX 4060 Laptop GPU", GpuAdapters.Recommended(cards)!.Key);
	}

	[Fact]
	public void OnATie_TheFirstCard_IsSuggested() {
		IReadOnlyList<GpuAdapter> cards = GpuAdapters.Keyed(new DxgiAdapter[] {
			new(0, "AMD Radeon RX 7900 XTX", 24 * GB, 0x1002, false),
			new(1, "AMD Radeon RX 7900 XTX", 24 * GB, 0x1002, false),
		});
		Assert.Equal(0, GpuAdapters.Recommended(cards)!.Index);
	}

	[Fact]
	public void NoCards_NothingSuggested() => Assert.Null(GpuAdapters.Recommended(Array.Empty<GpuAdapter>()));

	[Theory]
	[InlineData("NVIDIA GeForce RTX 4070", 0)]
	[InlineData("nvidia geforce rtx 4070", 0)]   // settings.json typed by hand
	[InlineData(" Intel(R) UHD Graphics 770 ", 1)]
	[InlineData("NVIDIA GeForce RTX 3080", null)] // swapped out since
	[InlineData("Microsoft Basic Render Driver", null)]
	[InlineData("", null)]                        // Windows' default
	[InlineData(null, null)]
	public void TheSettingsCard_IsFoundByName(string? key, int? index) =>
		Assert.Equal(index, GpuAdapters.Find(key, GpuAdapters.Keyed(Desktop))?.Index);

	[Fact]
	public void NoChoice_LeavesWindowsDefault_ForDirectMLAndFFmpeg() {
		GpuAdapters.Choose(null);
		Assert.Null(GpuAdapters.Chosen);
		Assert.Null(GpuAdapters.DeviceString); // FFmpeg's own default: DXGI's first
		Assert.Equal(0, GpuAdapters.DirectMLDevice);
		Assert.Equal("", GpuAdapters.Describe()); // heiward.log as before
	}

	[Fact]
	public void ACardThatIsntHere_LeavesWindowsDefault() {
		GpuAdapters.Choose("A graphics card this PC has never had");
		try {
			Assert.Null(GpuAdapters.Chosen);
			Assert.Null(GpuAdapters.DeviceString);
			Assert.Equal("; GPU work on Windows' default card (A graphics card this PC has never had isn't on this PC now)", GpuAdapters.Describe());
		}
		finally {
			GpuAdapters.Choose(null);
		}
	}

	[Fact]
	public void ThisPCsCards_AreListed_EachOnce() {
		IReadOnlyList<GpuAdapter> cards = GpuAdapters.List();
		Assert.Equal(cards.Count, cards.Select(c => c.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());
		Assert.Equal(cards.Count, cards.Select(c => c.Index).Distinct().Count());
		Assert.DoesNotContain(cards, c => c.VendorId == 0x1414);
	}

	[Fact]
	public void ACardChosenByName_IsTheOneDirectMLAndFFmpegAreGiven() {
		IReadOnlyList<GpuAdapter> cards = GpuAdapters.List();
		if (cards.Count == 0) return; // no DXGI here (not Windows, or no graphics driver)
		GpuAdapters.Choose(cards[^1].Key);
		try {
			Assert.Equal(cards[^1], GpuAdapters.Chosen);
			Assert.Equal(cards[^1].Index.ToString(System.Globalization.CultureInfo.InvariantCulture), GpuAdapters.DeviceString);
			Assert.Equal(cards[^1].Index, GpuAdapters.DirectMLDevice);
			Assert.Equal("; GPU work on the " + cards[^1].Key, GpuAdapters.Describe()); // what someone with two cards sends in heiward.log
		}
		finally {
			GpuAdapters.Choose(null);
		}
	}
}
