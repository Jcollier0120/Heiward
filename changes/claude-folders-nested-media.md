**Folders with photos or videos show before your first scan, and every folder's right-click menu has Scan now.**

### What changed

- Before a drive's first scan, a folder with photos or videos in it, or in any folder below it, is listed as usual. Before, every folder below a drive's top level was folded under **Show N folders without photos or videos**, even when it held photos.
- Such a folder says **Not scanned yet** where the number of photos and videos goes, on its page and in the Folders list. Before, it said "0 photos and videos". A scan counts them.
- A folder of photos you add after a scan is listed and says **Not scanned yet** too, until the next scan counts it.
- **Show N folders without photos or videos** now holds only folders with no photos or videos at any depth. Heiward checks by file type, upper-case names like IMG_0001.JPG included. It skips the same folders a scan skips, such as program and app folders, code projects and links to other folders, so their pictures don't count.
- On a very large folder, Heiward stops checking after about a second, so the page never waits long. If no scan has read that drive yet, the folder is listed. Otherwise the last scan's count stands.
- Right-clicking a folder below a drive, in the folder tree or the Folders list, now offers **Scan now**, as the hint above the list says. It scans just that folder and the folders in it. Before, it was missing from those menus.

### Before you update

- Nothing: it updates itself as usual.
