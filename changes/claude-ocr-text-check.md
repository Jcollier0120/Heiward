minor: true

**Two pictures are copies only when their words match too: two screenshots of a chat one message apart are no longer offered as copies.**

### What's new

- Heiward reads the words in photos that look like copies of each other, with a small text-reading model on your PC. Two screenshots of a chat one message apart, two receipts with different totals, or a photo whose caption says another year look the same pixel for pixel, but their words differ: they're now shown as **Looks the same, but its words differ**, for you to judge, and never checked for the Recycle Bin or cleaned up automatically.
- Pictures in the same layout whose words differ throughout, like two pages of one form, aren't shown as a set at all.
- A smaller, recompressed or HEIC copy of a screenshot is still a copy: a word read a little differently in a smaller picture, a space or a stray mark doesn't count. Another figure (a total, a date, a time, a page number) always does.
- It reads on the NPU of a Snapdragon PC or of an Intel Core Ultra with the NPU 3720 when the AI runs there, otherwise gently on the processor in the background. Each photo is read once and remembered while it's unchanged, so later scans only read what changed.

### Before you update

- The update downloads the text-reading model (about 4 MB) with the AI components. Until it's there, pictures are judged by their look alone, as before.
- The words read stay on your PC, in `texts.json` in Heiward's folder, and only for the photos in the latest report. The privacy policy says so.
- Your first scan after updating sorts its sets again and reads the photos that look alike once, which takes a little longer than usual.
