**Heiward looks closer before it checks a photo for you, so some pictures it used to check are now yours to judge.**

### What changed

- A photo is checked for you only when it's the same picture up close, not just on the whole. A copy with a face or a number plate blurred, a signature, an annotation or a date stamp added, or one line of a chat screenshot changed used to pass for "Saved again (same picture)": it could be checked for you, or removed by automatic cleanup. It now shows as an edited version for you to judge. Resized, recompressed and resaved copies are still checked for you.
- When a bigger picture matches a smaller one that has the camera's capture date, or is clearly older, the bigger one may be an enlarged export (an AI upscale, a print-size copy). The smaller one now shows as "Smaller, but may be the original: your call", and nothing checks or cleans it up for you.
- Before anything goes to the Recycle Bin, the copy that stays must be as the scan saw it, and must really be another file. A hard link, or the same folder seen through another drive letter, no longer counts as a copy, and a scan no longer lists such a pair.
- A file over 256 MB listed as identical is read through to the end beside the copy you keep before it's removed. If any byte differs (an unfinished or damaged copy), it stays, and Heiward tells you why.
- A copy whose path is too long for the Recycle Bin is refused instead of being deleted for good.
- A photo edited in place without changing its size (a BMP or TIFF saved again in Paint) is analysed again, instead of keeping the fingerprints of the picture it was.

### Before you update

- The first scan after updating takes a closer look at each pair of photos that may be plain copies: roughly a minute for every two thousand pairs. Later scans reuse what it saw. Nothing else is analysed again; from now on a photo is analysed again whenever its last-modified time changes.
- Some sets that were checked for you now wait for you, and automatic cleanup leaves those alone. Copies on a drive that's scanned only when you ask are checked for you again after the next scan that reads that drive.
