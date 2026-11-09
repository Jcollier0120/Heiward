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

/// <summary>
/// The text check's comparison: what a misread changes doesn't split a copy, what a person changed does. The misreads
/// are ones the copy probe saw on the reader's own output (a half-size screenshot's "sRGB" read "sRG8", a quarter-size
/// form's "reference" read "roferenco").
/// </summary>
public class TextMatchTests {
	/// <summary>A reading as the reader keeps it: one box a line, every word read sure unless it says otherwise.</summary>
	static string Read(params string[] lines) => TextMatch.Serialize(lines.Select(l => new[] {
		l.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => new TextWord(w, 0.98f)) }));

	static TextVerdict Verdict(string a, string b) => TextMatch.Compare(a, b).Verdict;

	[Theory]
	[InlineData("sRGB", "sRG8")]
	[InlineData("TOTAL", "T0TAL")]
	[InlineData("1080", "1O8O")]
	[InlineData("Café", "Cafe")]
	[InlineData("modern", "modem")]
	[InlineData("$15.55", "S15.ss")]
	[InlineData("EGGS", "EG6S")]
	public void ShapesTheReaderConfuses_CompareAsOne(string a, string b) =>
		Assert.Equal(TextMatch.Normalize(a), TextMatch.Normalize(b));

	[Fact]
	public void TheSameWords_AreTheSame_AndSoAreNone() {
		Assert.Equal(TextVerdict.Same, Verdict(Read("Refresh rate 60 Hz"), Read("Refresh rate 60 Hz")));
		Assert.Equal(TextVerdict.Same, Verdict("", ""));
		Assert.Equal(TextVerdict.Same, Verdict(null, ""));
	}

	[Fact]
	public void ASpaceReadOrLost_DoesNotCount() =>
		Assert.Equal(TextVerdict.Same, Verdict(Read("Refresh rate 60 Hz Night light"), Read("Refreshrate 60 Hz Night li ght")));

	[Fact]
	public void ALetterOrTwoMisread_DoesNotCount() =>
		Assert.Equal(TextVerdict.Same, Verdict(
			Read("Application for a residential parking permit", "Full name Payment reference Driving licence"),
			Read("Application for a residential parking permit", "Full narme Payment roferenco Driving licence")));

	[Fact]
	public void ManyMisreads_Count() =>
		Assert.Equal(TextVerdict.Differs, Verdict(
			Read("Full name Payment reference Driving licence Address Postcode Telephone"),
			Read("Fall nane Paymant raferenco Drivang licance Addrass Postcade Telephona")));

	[Theory]
	[InlineData("TOTAL 12.60", "TOTAL 13.20")]   // another total
	[InlineData("Lisbon, summer 2019", "Lisbon, summer 2021")] // another year
	[InlineData("Refresh rate 60 Hz", "Refresh rate 144 Hz")]
	[InlineData("Page 1 of 2", "Page 2 of 2")]
	public void AnotherFigure_AlwaysCounts(string a, string b) =>
		Assert.Equal(TextVerdict.Differs, Verdict(Read("FRESH MART 12 Market Street", a), Read("FRESH MART 12 Market Street", b)));

	[Fact]
	public void AnotherMessage_Counts() =>
		Assert.Equal(TextVerdict.Differs, Verdict(
			Read("Are we still on for Saturday?", "Yes see you at the station"),
			Read("Are we still on for Saturday?", "Can we make it Sunday instead")));

	[Fact]
	public void AWordAdded_Counts() =>
		Assert.Equal(TextVerdict.Differs, Verdict(Read("I am coming tonight"), Read("I am not coming tonight")));

	[Fact]
	public void OtherWordsThroughout_AreAnotherDocument() =>
		Assert.Equal(TextVerdict.Unrelated, Verdict(
			Read("Full name Address Postcode Telephone Email Date of birth"),
			Read("Vehicle registration Make and model Colour Permit zone Signature")));

	[Fact]
	public void ABoxReadUnsure_IsLeftOut() {
		// A photo's brickwork read as letters, beside its caption: only the caption counts.
		string caption = TextMatch.Serialize(new[] { new[] {
			new[] { new TextWord("56kt", 0.39f), new TextWord("'ee2", 0.38f), new TextWord("P%", 0.31f), new TextWord("e", 0.94f) },
			new[] { new TextWord("Lisbon,", 0.99f), new TextWord("summer", 0.97f), new TextWord("2019", 1f) } } });
		Assert.Equal(TextVerdict.Same, Verdict(caption, Read("Lisbon, summer 2019")));
	}

	[Fact]
	public void AStretchReadUnsure_DoesNotCount() {
		string unsure = TextMatch.Serialize(new[] { new[] { new[] {
			new TextWord("Display", 0.99f), new TextWord("resolution", 0.98f), new TextWord("Advanced", 0.85f), new TextWord("display", 0.99f) } } });
		string other = TextMatch.Serialize(new[] { new[] { new[] {
			new TextWord("Display", 0.99f), new TextWord("resolution", 0.98f), new TextWord("Adjusted", 0.8f), new TextWord("display", 0.99f) } } });
		Assert.Equal(TextVerdict.Same, Verdict(unsure, other));
	}

	[Fact]
	public void AReading_KeepsItsBoxesWordsAndConfidences() {
		string read = TextMatch.Serialize(new[] { new[] { new[] { new TextWord("Hello", 0.91f), new TextWord("there", 0.5f) }, new[] { new TextWord("9:14", 1f) } } });
		var boxes = TextMatch.Boxes(read);
		Assert.Equal(2, boxes.Count);
		Assert.Equal(new TextWord("there", 0.5f), boxes[0][1]);
		Assert.Equal("Hello there 9:14", TextMatch.Plain(read));
	}

	[Fact]
	public void PicturesOfOneShape_AreReadAtTheSmallersSize_NoBiggerThanTheCap() {
		Assert.Equal(((960, 540), (960, 540)), TextCheck.Sizes((1920, 1080), (960, 540)));
		Assert.Equal(((1536, 2048), (1536, 2048)), TextCheck.Sizes((3024, 4032), (3024, 4032)));
		// A crop: each at its own size.
		Assert.Equal(((1920, 1080), (1080, 1080)), TextCheck.Sizes((1920, 1080), (1080, 1080)));
	}

	[Fact]
	public void ACropOfAPage_IsNeverAnotherDocument() {
		string page = Read("Full name Address Postcode Telephone Email Date of birth");
		string other = Read("Vehicle registration Make and model Colour Permit zone Signature");
		Assert.Equal(TextVerdict.Unrelated, TextCheck.Judge(page, other, sameShape: true));
		Assert.Equal(TextVerdict.Differs, TextCheck.Judge(page, other, sameShape: false));
	}

	[Fact]
	public void TheSelfCheckPicture_Decodes() {
		using Stream s = typeof(OcrReader).Assembly.GetManifestResourceStream("HEI.Core.Assets.text-selfcheck.png")!;
		using var bytes = new MemoryStream();
		s.CopyTo(bytes);
		TextPicture? pic = TextPicture.Load(bytes.ToArray());
		Assert.NotNull(pic);
		Assert.Equal((720, 150), (pic!.Width, pic.Height));
		TextPicture half = pic.Resize(360, 75);
		Assert.Equal(360 * 75 * 3, half.Bgr.Length);
	}
}
