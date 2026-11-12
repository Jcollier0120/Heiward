**The folder view shows how much space each folder takes, and each drive says whether Heiward scans it by itself.**

### What's new

- Each folder in the folder tree, and in the Folders table on a drive's or a folder's page, shows its size on disk, so you can see where your space goes. Folders Heiward doesn't scan show their size too, and so does the row that folds them away ("14 exempt folders"). The Folders table has a Size column you can sort by.
- A drive shows its used space and free space as soon as it's listed. A folder's size shows "…" until it has been measured. Heiward measures only the folders you have open, in the background, so the page never waits for it. A big folder, such as Windows or your user folder, can take a minute.
- Sizes are what the files take on this PC's drive, as File Explorer's "Size on disk" counts them. A file kept only online in OneDrive counts as next to nothing until it's downloaded, and opening the page downloads nothing. Links to other folders are not followed, so nothing is counted twice. A folder Windows won't let Heiward read is skipped, and the folder's tooltip says how many were skipped.
- Each drive in the folder tree has a tag: **Auto** if scheduled scans read it, **Manual** if only a scan you start does. Hover over the tag to see when scans run (for example every 6 hours on AC power), when the next one is due, and how to change it: right-click the drive.

### What changed

- A drive with nothing found yet is no longer greyed out in the folder tree, so it no longer looks like a drive Heiward leaves out.
- If you chose at setup that Heiward scans only when you press Scan now, every drive now shows **Manual**. Its tooltip and its right-click menu say there are no scheduled scans. Before, the right-click menu offered "Scan only when I ask", as if scheduled scans still read the drive. The menu now offers "Scan now" and "Leave out of Scan now".
- With scans only when you press Scan now, a scheduled scan left over from an earlier setup now ends at once without reading any drive.

### Before you update

- Nothing: it updates itself as usual. Heiward keeps folder sizes in memory for 15 minutes and saves nothing new on your PC.
