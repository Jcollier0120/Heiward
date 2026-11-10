# Look-alike photos: the bar, set before any score

Heiward groups look-alike photos so the person can delete the extras. People delete from these sets, so a
different shot grouped as a duplicate can cost someone a photo. The AI threshold (94%) and the review's
labels were tuned by hand, never measured on a set: an edit scored 96.2 and a different shot of one scene
94.3, close enough that different shots can be grouped. This file fixes the bar, the set and the held-out
split before anything is scored or tuned. It is committed before the first run.

## The bar

At the default setting (the review page's, what every Manor install runs: AI matching on, on the CPU here,
gray match 96%, AI match 94%):

1. **A different shot is never marked a duplicate: 0 pairs on the whole set** (tune and held-out together).
2. Within that, **as many edits found as possible**, per kind of edit: crops, re-saves, resizes, filters and
   small rotations.
3. No worse anywhere: what the review pre-checks today (plain copies) stays pre-checked, and the existing
   tests keep passing.

The work merges only if 1 holds, or if it is better and no worse anywhere. Otherwise it stays a draft.

## The set

- **Originals:** 369 photos from Wikimedia Commons, in 150 series by 83 photographers
  (`HEI.Benchmarks/Lookalike/commons-set.json` lists each file's page, URL, size, license and author).
  The licenses are CC BY-SA 4.0 (322), CC0 (39), CC BY 4.0 (6) and CC BY-SA 2.0 (2); none is NC or ND.
  - A series is one uploader's shots with one camera, each taken (EXIF capture time) at most 120 s after
    the one before: different shots of one scene, the case that scores like an edit. They were listed from
    the Wiki Loves Earth, Monuments, Folklore, Africa and Love upload categories.
  - Each series keeps its first 3 shots of 12 MB or less, so it has 2 or 3. An uploader has at most 3
    series, and series of more than 20 shots (time-lapses, bulk uploads) are left out.
  - Two uploads with one capture time count as one shot, not two: the first is kept.
- **Edits:** made from every original by `HEI.Benchmarks/Lookalike/make-edits.py` (fixed seed), 16 kinds:
  - crops: `crop-90` (centred, 95% of each side), `crop-75` and `crop-50` (off-centre, 87% and 71% of each
    side, towards a corner picked by the seed), `crop-square` (the largest centred square);
  - re-saves: `resave-q85`, `resave-q60`, `resave-q35` (JPEG quality);
  - resizes: `resize-50` (half each side, quality 85), `resize-1080` (long side 1080, quality 75, as a
    messenger sends it);
  - filters: `filter-bw` (black and white), `filter-warm` (a warm tone), `filter-vivid` (colour +40%,
    contrast +15%), `filter-bright` (+20%), `filter-blur` (a light blur, 0.2% of the long side);
  - small rotations: `rotate-2` (+2°) and `rotate-5` (−5°), each cut to the largest upright rectangle of
    the picture's shape inside it.
  - Every edit is saved as a JPEG without the original's EXIF, as a messenger or a quick export does. That
    is the worst case for a check that reads capture times. The originals keep theirs.
- **Names:** every file gets a neutral name (a hash) in one folder. Names (burst numbering, "-edited") then
  can't help, and what is measured is the pictures and the facts inside the files.

## Pairs and what counts

- **Same picture:** an original and one of its edits, counted per kind. Two edits of one original are the
  same picture too: they're reported, but they aren't in the bar.
- **Different shots:** any two files from two different shots, originals or edits. They're reported as
  *same scene* (one series) and *other* (two series).
- **Marked a duplicate:** the review page puts the two files in one set. Being in a set is enough: "your
  pick" sets are where people delete. What it pre-checks is reported as well, and so are the engine's own
  groups (what the app and the command line show at the same settings).
- **Edit found:** the edit is in one set with its original.

## Held-out split

By uploader, so the held-out photographers are never seen while tuning: an uploader is held out when the
first byte of `sha256("heiward-lookalike|" + uploader)` is a multiple of 3. That is 49 series (120 shots)
held out, and 101 series (249 shots) to tune on.

- Thresholds and rules are chosen on the tune split's numbers alone.
- The held-out split is scored at each step of the ladder, after the step's values are fixed, and is never
  used to pick them.
- Bar 1 is checked on both splits together.

## The ladder, cheapest first, scored after each step

1. The thresholds, and the facts the code already has: EXIF capture time and camera, file size and
   dimensions, and the compare index's signals.
2. Combined signals, such as a second check for pairs in the close band.
3. Only then, changes to the model.

## Also reported

- A short curve of the other settings: different shots marked and edits found as the AI threshold moves.
- The compare phase's time at 50,000 photos, against #311's 1.2 s.
- Bytes written per photo and per idle day at 50,000 and 100,000 photos, before and after.
