**Scanning another PC no longer leaves anything behind on the PC that did the scanning; the results stay on the PC that was scanned.**

### What changed

- Scanning another PC no longer leaves anything behind on the PC that did the scanning; the results stay on the PC that was scanned. Before, the PC doing the scan kept everything it needed for the next scan in Heiward's folder: an empty placeholder for each of the other PC's photos and videos, its database, the list of that PC's files, and what it found. On one PC that came to 670,803 files: 3.1 TB as Explorer counts them, and about 2 GB of the disk. Now the PC that was scanned keeps what the scan found and what it needs next time, in its own Heiward folder. The PC scanning has a photo of the other PC's only while it reads it, and gives everything back when the scan ends, whether it finished, failed or was stopped.
- A scan with nothing new since the last one still fetches nothing, as in 1.11.4. A scan stopped part way still picks up where it stopped: the PC that was scanned hands back what the last scan kept, so the files it checked aren't read again.
- If the scanned PC can't be reached when a scan ends, the PC that did the scan keeps what it found and offers it back every half hour until that PC takes it. Its page says so meanwhile, and says when Heiward on the other PC needs updating first.
- The card's step that made room for the other PC's files is gone: a scan starts checking its files straight away, where on a big library it used to make hundreds of thousands of placeholders first.

### Before you update

- Update Heiward on both PCs. As before, a scan needs the same version on both, and the page says which PC to update.
- Nothing else to do. After the update, the PC that did the scanning clears what older versions left there by itself, in the background: it starts a couple of minutes after Heiward does, works at the lowest disk priority a batch at a time, and pauses while a game is played. It first hands back what the scanned PC doesn't have yet, so later scans still pick up from there, and it never touches a scan that is running. If Heiward stops before it's done, it carries on next time. On a big library this can take a while; Heiward's log says when it's done.
- The PC that was scanned now holds the scan's records in its Heiward folder: several hundred MB for a very big library.
