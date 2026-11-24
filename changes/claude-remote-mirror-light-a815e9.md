**A scan of another of your PCs no longer leaves a file here for every file of that PC.**

### What changed

- To scan another of your PCs, Heiward used to make an empty placeholder here for each of that PC's photos and videos, and kept them all after the scan. On one PC that was 670,803 files under Heiward's folder: 3.1 TB as Explorer counts them, about 1.4 GB of the disk with Windows' records of them. Now a file of the other PC is here only while it's read, or fetched just ahead of that, and is deleted once it's read. Nothing of that PC's files is left once the scan ends. What the scan keeps is a dozen small files of its own: its database, the list of the other PC's files, and what it found.
- A scan with nothing new since the last one still fetches nothing, as in 1.11.4.
- The card's step that made room for the other PC's files is gone: a scan starts checking its files straight away, where it used to make hundreds of thousands of placeholders first on a big library.

### Before you update

- Nothing to do. After the update, Heiward clears the placeholders older versions left, by itself, in the background: a couple of minutes after it starts, at the lowest disk priority, a batch at a time, and it pauses while a game is played. It never touches a scan that is running, or anything else of Heiward's. If Heiward stops before it's done, it carries on the next time. On a big library this can take a while; Heiward's log says when it's done.
