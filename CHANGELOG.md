# Heiward's changelog

Each version of Heiward, newest first: what's new, what changed, and what to do before updating.

## 1.10.11

**Folder pages come back sooner after you remove many sets.**

### What changed

- After **Move to Recycle Bin**, **Skip all**, or a set's own **Move** or **Keep**, the folder's page and the folder tree are drawn again in a fraction of the time they took before. The more sets Heiward has found, the bigger the difference.
- Heiward used to read its whole list of sets again for every folder it showed, one folder after another. Now it keeps the list in memory and reads it again only after a scan has changed it. The page also asks for the folders open in the tree together.
- The same goes for thumbnails, and for each set as it's moved, so removing many sets takes less time too.
- On a hard disk, Heiward still looks at the open folders one at a time, so they don't slow each other down. Several drives, and SSDs, are looked at together.
- Heiward keeps the list in memory only while you use its page. A few minutes after you close the page, it lets go of that memory.

### Before you update

- Nothing: it updates itself as usual.

## 1.10.10

**Brings 3 changes: Scan this drive now reads just that drive; A spinner while Heiward removes sets, from your click until the page is ready again; Take Heiward's tour again from About or Settings.**

### What's new

- **Take the tour** is in About (the (i) button beside Settings), and at the bottom of Settings, under Everything else. It walks you through the page again: what Heiward does, the choices that are yours, and each part of the page. Before, the tour could only be reopened by typing an address, and Heiward's window has no address bar.
- Finish or skip it and you're on the home page, on the tab you had open. Back takes you to where you were. Take it as often as you like. Taking it again doesn't change when the tour opens by itself, which happens only after a new install.

### What changed

- **Scan this drive now**, on a drive's page, now reads only that drive. Before, it also scanned every drive Heiward scans by itself. That was true even on a drive Heiward scans only when you ask. The other drives stay as their last scans found them, so a disk that sleeps stays asleep, and their duplicates stay listed.
- Only that drive's "Last scanned" changes. The scheduled scans keep their times and still read every drive they read. **Scan now instead of at 6:00 PM** is still the button that stands in for the next scheduled scan.
- On a drive Heiward scans only when you ask, Scan this drive now still brings that drive's developer items up to date at the same time.
- If you chose at setup that Heiward scans only when you press Scan now, **Scan this drive now** reads only that drive too. **Scan now** still reads them all.
- After updating, if your first scan reads just one drive or one folder, the other drives now keep their "Last scanned" time. Before, they could show "Last scanned: never" until the next full scan.
- **Move to Recycle Bin**, **Skip all**, and a set's own **Move** and **Keep** buttons now spin from the moment you confirm until the folder's page has been drawn again without those sets. With many sets, redrawing the page is the slow part, and before this nothing showed while it ran.
- While a removal runs, the folder's page takes no clicks and shows the busy cursor, so the same sets can't be removed twice. The page no longer redraws partway through and puts the button back.
- What the removal did ("Moved 40 files, 1.2 GB, to the Recycle Bin.") shows while the page is redrawn, then stays under the command bar for a while. If any file couldn't be moved, it shows in red.
- If something goes wrong, the spinner stops and the buttons can be pressed again.
- Each time you take the tour, it shows the parts of the page that are there now. If you have turned on game mode or developer mode since your last tour, it includes the Games or Developer area.

### Before you update

- Nothing: it updates itself as usual.

## 1.10.9

**Brings 5 changes: Each drive's page says when the drive was last scanned and when it's scanned next, and you can bring the next scheduled scan forward; The folder view shows how much space each folder takes, and each drive says whether Heiward scans it by itself; Scan just one folder; Setting Heiward up fits on the screen, and its window remembers where it was; Heiward's tour now opens after every new install, including on a PC that had Heiward before.**

### What's new

- A drive's page shows when the drive was last scanned ("never" before its first scan) and when the next scheduled scan is. For a drive Heiward scans only when you ask, or if you chose at setup that Heiward scans only when you press Scan now, it says no scans are scheduled.
- **Scan this drive now** is on every drive's page, including drives Heiward scans by itself. On those drives it scans every drive Heiward scans by itself, and scheduled scans keep their times.
- On a drive Heiward scans by itself, **Scan now instead of at 6:00 PM** brings the next scheduled scan forward. Heiward scans straight away, and once that scan has finished, the 6:00 PM scan is skipped. The one after runs as usual. If the scan fails or you stop it, nothing is skipped and the 6:00 PM scan still runs. The drive's page says which scheduled scan is skipped, and the drive's tag gives the next scan after it.
- Each folder in the folder tree, and in the Folders table on a drive's or a folder's page, shows its size on disk, so you can see where your space goes. Folders Heiward doesn't scan show their size too, and so does the row that folds them away ("14 exempt folders"). The Folders table has a Size column you can sort by.
- A drive shows its used space and free space as soon as it's listed. A folder's size shows "…" until it has been measured. Heiward measures only the folders you have open, in the background, so the page never waits for it. A big folder, such as Windows or your user folder, can take a minute.
- Sizes are what the files take on this PC's drive, as File Explorer's "Size on disk" counts them. A file kept only online in OneDrive counts as next to nothing until it's downloaded, and opening the page downloads nothing. Links to other folders are not followed, so nothing is counted twice. A folder Windows won't let Heiward read is skipped, and the folder's tooltip says how many were skipped.
- Each drive in the folder tree has a tag: **Auto** if scheduled scans read it, **Manual** if only a scan you start does. Hover over the tag to see when scans run (for example every 6 hours on AC power), when the next one is due, and how to change it: right-click the drive.
- Right-click a folder on the review page, on its drive's card or in the folder list, and choose **Scan now**. Heiward reads only that folder and the folders inside it, and the rest of every drive stays as its last scan found it.
- On a drive that's scanned only when you ask, a folder's page now has **Scan this folder now**, which reads that folder and the folders inside it while the disk stays asleep for the rest. **Scan the whole drive** is still there.
- A folder's scan isn't the drive's: the drive's "Last scanned" and the next scheduled scan stay as they were.
- Heiward's window opens where you left it, at the size you left it, and maximized if it was. The first time, it opens in the middle of the screen at a size that fits.
- Setting up asks about game mode and developer mode, so you don't have to find them in Settings later.
- On a PC with no NPU, the pictures shown while Heiward is set up show the graphics card, where its AI runs there.

### What changed

- A drive with nothing found yet is no longer greyed out in the folder tree, so it no longer looks like a drive Heiward leaves out.
- If you chose at setup that Heiward scans only when you press Scan now, every drive now shows **Manual**. Its tooltip and its right-click menu say there are no scheduled scans. Before, the right-click menu offered "Scan only when I ask", as if scheduled scans still read the drive. The menu now offers "Scan now" and "Leave out of Scan now".
- With scans only when you press Scan now, a scheduled scan left over from an earlier setup now ends at once without reading any drive.
- Setting up fits on the screen without scrolling, on 1366 x 768 and 1920 x 1080 screens at their usual scalings. Its questions sit side by side in a wide window, the banner gets slimmer in a short one, and the buttons stay at the bottom, clear of everything.
- The pictures shown while Heiward is set up have larger words, so they're easier to read.
- The first time you open Heiward's page after installing it, from the Microsoft Store or from GitHub, its tour walks you through the page and ends by asking about the first scan. On a PC where Heiward had been installed before, the tour never opened again, even after uninstalling and installing it fresh: the browser remembered that the tour had been seen, and uninstalling Heiward doesn't clear what the browser remembers.
- Now Heiward itself keeps track. Installing it shows the tour once, the first time the page opens after setup. Once you finish or skip it, it doesn't open by itself again, after a restart or an update. Uninstall Heiward and install it again, and the tour opens again.
- You can still take the tour any time at `#/tour` on the page.
- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual. Heiward now saves two small files with its settings: when a scan last read each drive, and, after you bring a scan forward, which scheduled scan to skip. Each is written once per scan, never per folder.
- Nothing: it updates itself as usual. Heiward keeps folder sizes in memory for 15 minutes and saves nothing new on your PC.

## 1.10.8

**Small fixes and improvements.**

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.10.7

**Small fixes and improvements.**

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.10.6

**Small fixes and improvements.**

### What changed

- Kit 2.152.13: What's new in About, and the changelog each release carries, now show only what is for the people who use the staff: the notes about how the staff are made are left out, as they are from Manor's update notes.
- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.10.5

**Brings 2 changes: On the review page, a softened (blurred) version of a photo is no longer pre-checked as a copy. And a document with a picture in black and white is no longer the same as one with that picture in colour; Heiward's release notes and changelog describe Heiward itself.**

### What changed

- The review page pre-checks the plain copies of a photo for the Recycle Bin: the same picture made smaller, more compressed or saved again. A version softened all over, as a blur or "soften" filter does, kept every part of the picture where it was, so up close it could still pass for a copy and be pre-checked. If you accepted the pre-checks, you'd have removed a picture that isn't a copy. Now Heiward also compares how sharp the two are. A softer version is shown as an **Edited version**, like other edits, and you decide whether to keep it. Automatic cleanup leaves it for you as well.
- On a test set of 369 photos, 3 blurred versions were pre-checked as copies. Now none are. Every copy that was pre-checked before still is (1,743), and which photos are grouped together, and which one is kept, is unchanged.
- Another program on this PC can ask Heiward whether two documents hold the same pictures, for instance to find duplicate documents. Heiward compared those pictures in gray, so a black-and-white picture was the same as its colour one, and two documents with the same words and the same picture, one in colour and the other in black and white, were the same document. Now a black-and-white version is an edit of the picture, not the same picture.
- On test Word and PDF documents with 24 pictures, all 48 colour and black-and-white pairs were called the same. Now none are. Every true copy is still the same (192 of 192): a picture saved again, exported smaller, or stored without loss. A warmer version of a picture can still be called the same there: the small pictures compared can't tell its tint from a copy's.
- A release's notes on GitHub, and the changelog, used to list the changes to the shared parts Heiward is built with, which aren't about Heiward. They're left out now, as the Microsoft Store's notes already left them out. A version that only brought those says "Small fixes and improvements."
- Kit 2.152.12: The staff's background work leaves the NPU to Windows' own AI models while they're using it, as it does to a call.

### Before you update

- Nothing: it updates itself as usual. The first check after the update compares the sharpness of the copies it lists, once, so it may take a little longer (about a minute more on the test set).

## 1.10.4

**Brings 2 changes: Heiward's review no longer lists another shot of the same view as a look-alike of your photo; Heiward's page asks its questions in its own dialog, not in the browser's pop-up windows.**

### What changed

- Two photos of one place, taken a step or a moment apart, can look as alike to Heiward's AI as a photo and its crop. Before it lists such a pair as look-alikes, Heiward now lines them up: it finds where one picture sits in the other, cropped, scaled or turned a little, then compares them part by part. One that doesn't line up is another shot, and it's left out of the set.
- On a test set of 6,273 photos, other shots listed as look-alikes went from 80 to none, and every crop, filter, turn and resized copy found before is still found.
- Copies, and edits the AI is sure of, are listed as before, without this check.
- The first review that meets such pairs takes a little longer, about 20 seconds for 1,400 pairs on a 24-core PC. The answers are kept, so later reviews don't look again.
- Every question the page asks before it acts (moving files to the Recycle Bin, deleting developer leftovers or merged branches, clearing the history, skipping look-alike sets, turning on automatic cleanup, removing a scan rule) now opens in the page's own dialog, in your theme's colours, light or dark. Before, the browser opened its own grey window.
- Naming a project when you group repositories is asked in the same dialog, with the suggested name selected and ready to type over.
- Each dialog's button says what it does, such as "Delete branches" or "Clear history". Where the answer can't be undone (deleted for good, deleted from the cloud too, or forgotten), that button is red and Cancel has the focus, so pressing Enter doesn't delete anything.
- Esc is Cancel, Tab stays within the dialog, and the page under it waits until you answer. If you go to another page, the question is cancelled.

### Before you update

- Nothing: it updates itself as usual.

## 1.10.3

**Heiward writes less to your disk while it scans: only the files that changed go into its list of each drive, and a scan's progress is saved every few seconds instead of every second.**

### What changed

- To see what changed since the last scan, Heiward keeps a list of the files on each drive. It used to save that whole list again after every scan. It now adds only the files that changed, and saves the list whole again only once those additions are as big as the list. On a library of 50,000 photos with 1% of them changed, that's 4.2 MB down to about 40 KB a scan.
- While a scan runs, its progress for Heiward's page is saved every 3 seconds while the page is open and every 30 seconds while it isn't, instead of every second. Each new step of a scan still shows on the page at once.
- Which photos are found as duplicates and look-alikes is unchanged.

### Before you update

- Nothing: it updates itself as usual.
- If you go back to an earlier version later, its first scan lists each drive again from the start, once. On a large drive that can take a minute or two, and nothing is missed.

## 1.10.2

**Brings 3 changes: At a manor, Heiward's first scan waiting for you is listed under When you have time, not This needs you; Heiward is gentler on your disk: a check that finds nothing new no longer rewrites what it already saved; On the review page, a black-and-white or warm-filtered version of a colour photo is no longer pre-checked as a copy.**

### What changed

- A new install of Heiward scans nothing until you say. Castellan's page used to show that in red under **This needs you**. It's now under **When you have time**, with the same words and link, and it's never a notification: nothing is lost while it waits.
- When a check finds the same duplicates as last time, Heiward no longer saves the whole list of them again, or the fingerprints of your files that it keeps. It notes only when the check ran. On a library of 50,000 photos, on a drive Heiward has to list again every 6 hours, that's about 17.5 MB written per check down to a few hundred bytes, up to 70 MB a day.
- Heiward's settings and status files are saved only when something in them changes.
- Which photos are found as duplicates and look-alikes is unchanged.
- The review page pre-checks the plain copies of a photo for the Recycle Bin: the same picture made smaller, more compressed or saved again. Heiward compared pictures in gray, so a black-and-white or warmer version of a colour photo looked the same as the photo and could be pre-checked as a copy of it. If you accepted the pre-checks, you'd have removed a picture that isn't a copy. Now Heiward compares the colours too. Such a version is shown as an **Edited version**, like other edits, and you decide whether to keep it. Automatic cleanup leaves it for you as well.
- On a test set of 369 photos, 92 black-and-white versions and 117 warm ones were pre-checked as copies. Now one is: its photo was already black and white, so the two are the same. Every copy of the same colours that was pre-checked before still is. What's no longer pre-checked is a colour copy in a set where the picture kept is a black-and-white or warm version (10 on the test set): it isn't a copy of that one.
- Which photos are grouped together, and which one is kept, is unchanged.

### Before you update

- Nothing: it updates itself as usual. The first check after the update compares the colours of the copies it lists, once, so it may take a little longer.

## 1.10.1

**Brings 4 changes: Big photo libraries now compare in seconds instead of minutes, with exactly the same results; Heiward's page uses a wide window better, and Back now goes back to the page you were on; Delete Highlighted and the cleanup dry run report are quick on big results; Heiward writes far less to your disk as it scans: a save writes only the photos that changed.**

### What's new

- On a wide window, Settings and About sit in two columns, and so does each part of the home page: the drives beside At a glance, and the duplicates and the history beside their totals.
- Before the first scan, Duplicates and History say what they'll show and what to do next, with a **Scan now** button, in place of small empty boxes.
- About now has a few lines on Castellan, the household staff Heiward is part of, with the Household plan's prices: 7 days free with no card, then $4 a month or $36 a year, and 25% off until December 11, 2026. Heiward stays free, and works the same without it. It's never shown when Castellan is installed.

### What changed

- Finding duplicates used to check every file against every other file, so the work grew with the square of the library: 50,000 photos meant 1.25 billion comparisons. Heiward now first finds, for each file, the few files that could possibly match it, and compares only those, in the same order and by the same rules as before. The groups, differences and flags it finds are exactly the same.
- Measured on a test library on the same PC: 50,000 photos now compare in 1.2 seconds (before: 17.6 seconds), 100,000 photos in 2.3 seconds (before: 91 seconds). With AI matching on: 2.5 seconds for 50,000 (before: 21) and 6.8 seconds for 100,000 (before: 108). Video libraries, mirrored-image matching and pHash matching get faster too.
- With "ignore black pixels" or "ignore white pixels" on, each comparison is now about 9 times faster on PCs with ARM processors (such as Snapdragon), which had been using a slow way to compare: 10,000 photos with "ignore black pixels" now take 0.9 seconds (before: 6.8 seconds). Dark or bright pictures are compared on only a few of their pixels, so they can match far more files, and Heiward still has to check more of those pairs: 50,000 photos with "ignore black pixels" take about 11 seconds.
- The log now says how many comparisons the compare phase made, out of how many, and how long it took.
- **Back** goes to the page you were on before, without stepping back through every folder you opened. **Forward** does the same the other way, and takes you to the last folder you had open. **Up** still goes one folder up, and from a drive's root back to This PC. Your browser's own Back button still goes one step at a time.
- The Developer and Games areas work the same way: Back leaves the area, and Up climbs from one of its parts to the area's first page.
- The title bar shows Heiward's icon and **Home**, which takes you to This PC from anywhere.
- The Castellan card is no longer on the home page, and its **Not now** and the switch for it in Settings are gone.
- The page's footer no longer shows an empty "Settings:" line.
- **Delete Highlighted** (Ctrl+Delete) takes the highlighted files off the list in one go. It used to take them off one at a time, searching the whole list for each one and updating the window's counts after each: 10,000 highlighted out of 50,000 froze the window for almost a second, and 20,000 out of 100,000 for about three seconds. It now takes about a third of a second for either, nearly all of it spent redrawing the list once. The files that stay keep their order, and **Undo Selection Change** (Ctrl+Z) works as before. As before, the files are only taken off the list, not deleted from disk.
- **Export Checked Items Cleanup Dry Run Report** sorts the list into its sets once, instead of searching the whole list for each set. A report covering 8,000 sets out of 50,000 files took over three seconds and one covering 16,000 sets out of 100,000 over twenty. Both now take under a tenth of a second. The report is the same file as before, with the same files in the same order.
- Heiward saves its list of scanned files and its AI cache as it scans: every five minutes during a long scan, and at the end of each scan that found something. Each save used to write the whole list and cache again, even for one new photo: about 92 MB for a library of 50,000 photos and 183 MB for 100,000. So a day of hourly scans that each found one new photo wrote about 2.2 GB, or 4.4 GB for 100,000 photos. A save now adds only what changed, under 2 KB for each new photo, and that day writes about 50 KB. Once the changes add up to a fifth of the list, Heiward writes the list whole once and starts adding again: on average about 7 KB more for each photo that changed. A scan that finds nothing new still writes nothing to them.
- If Heiward is stopped in the middle of a save (the PC loses power, or the process is ended), the list and the cache stay as the last finished save left them, and the next scan carries on from there.

### Before you update

- Nothing: it updates itself as usual. If you had hidden the Castellan card with **Not now**, that choice is no longer kept: the card is only in About now, which you open yourself.
- Nothing: it updates itself as usual. If you go back to an older version, it reads the list as it was last written whole, so its next scan reads again the photos that changed since then (at most a fifth of them). Nothing is lost.

## 1.10.0

**Brings 3 changes: Heiward looks closer before it checks a photo for you, so some pictures it used to check are now yours to judge; Two pictures are copies only when their words match too: two screenshots of a chat one message apart are no longer offered as copies; Heiward's page answers only your own Windows account, and the Developer area no longer runs git where a repository's own settings could run a program.**

### What's new

- Heiward reads the words in photos that look like copies of each other, with a small text-reading model on your PC. Two screenshots of a chat one message apart, two receipts with different totals, or a photo whose caption says another year look the same pixel for pixel, but their words differ: they're now shown as **Looks the same, but its words differ**, for you to judge, and never checked for the Recycle Bin or cleaned up automatically.
- Pictures in the same layout whose words differ throughout, like two pages of one form, aren't shown as a set at all.
- A smaller, recompressed or HEIC copy of a screenshot is still a copy: a word read a little differently in a smaller picture, a space or a stray mark doesn't count. Another figure (a total, a date, a time, a page number) always does.
- It reads on the NPU of a Snapdragon PC or of an Intel Core Ultra with the NPU 3720 when the AI runs there, otherwise gently on the processor in the background. Each photo is read once and remembered while it's unchanged, so later scans only read what changed.

### What changed

- A photo is checked for you only when it's the same picture up close, not just on the whole. A copy with a face or a number plate blurred, a signature, an annotation or a date stamp added, or one line of a chat screenshot changed used to pass for "Saved again (same picture)": it could be checked for you, or removed by automatic cleanup. It now shows as an edited version for you to judge. Resized, recompressed and resaved copies are still checked for you.
- When a bigger picture matches a smaller one that has the camera's capture date, or is clearly older, the bigger one may be an enlarged export (an AI upscale, a print-size copy). The smaller one now shows as "Smaller, but may be the original: your call", and nothing checks or cleans it up for you.
- Before anything goes to the Recycle Bin, the copy that stays must be as the scan saw it, and must really be another file. A hard link, or the same folder seen through another drive letter, no longer counts as a copy, and a scan no longer lists such a pair.
- A file over 256 MB listed as identical is read through to the end beside the copy you keep before it's removed. If any byte differs (an unfinished or damaged copy), it stays, and Heiward tells you why.
- A copy whose path is too long for the Recycle Bin is refused instead of being deleted for good.
- A photo edited in place without changing its size (a BMP or TIFF saved again in Paint) is analysed again, instead of keeping the fingerprints of the picture it was.
- On a PC where more than one person signs in, each Windows account's Heiward page now answers only that account. Before, another account on the same PC could open your Heiward page and see your photos' thumbnails and paths, or clean them up. Now every other account, and any program of theirs, is refused.
- If another account's Heiward already uses the usual port (18484), your Heiward takes a free port of its own and remembers it, so both accounts have their own page. The Start menu, the Heiward shortcut and notifications open yours.
- Developer mode: Heiward no longer runs git in a repository whose own git settings could make git run a program, or reach another computer, such as a folder unzipped from the internet with a `.git` inside. That repository shows **Not checked with git** and why, and nothing in it is offered for cleanup. Every git Heiward runs now has git's file-watcher program and hooks turned off.
- Developer mode: a worktree record that points to a folder on another computer (a network share) is ignored.
- Developer mode: your Windows sign-in goes only to the Azure DevOps Server (TFS) hosts you list in `windowsSignInHosts` in `settings.json`, never to a server a repository names by itself. Other servers use the sign-in git keeps for them.
- The privacy policy now says that on AMD NPUs, Windows ML downloads the chip's NPU package from Microsoft itself.

### Before you update

- The first scan after updating takes a closer look at each pair of photos that may be plain copies: roughly a minute for every two thousand pairs. Later scans reuse what it saw. Nothing else is analysed again; from now on a photo is analysed again whenever its last-modified time changes.
- Some sets that were checked for you now wait for you, and automatic cleanup leaves those alone. Copies on a drive that's scanned only when you ask are checked for you again after the next scan that reads that drive.
- The update downloads the text-reading model (about 4 MB) with the AI components. Until it's there, pictures are judged by their look alone, as before.
- The words read stay on your PC, in `texts.json` in Heiward's folder, and only for the photos in the latest report. The privacy policy says so.
- Your first scan after updating sorts its sets again and reads the photos that look alike once, which takes a little longer than usual.
- If more than one Windows account on your PC uses Heiward, the second account's page may move to a new port. If that account uses Castellan, Castellan's Heiward card in that account may show Heiward as not answering, because it looks on the usual port. Open Heiward from the Start menu instead.
- Developer mode with an Azure DevOps Server (TFS) at work: to keep seeing its pull requests with your Windows sign-in, add the server's name to `windowsSignInHosts` in `%LOCALAPPDATA%\Heiward\settings.json`, for example `"windowsSignInHosts": ["tfs"]`. Or sign in to it once with git (Git Credential Manager).
- Developer mode: repositories that set `core.hooksPath` (for example with husky), `core.fsmonitor`, a filter or a credential helper in their own git settings now show **Not checked with git**, and their worktrees and merged branches aren't offered for cleanup.

## 1.9.138

**Heiward from the Microsoft Store gets a new version once a week, and its Store page says what's new in it.**

### What changed

- Heiward from the Microsoft Store gets the newest version once a week, sent on Fridays at 10:00 New York time. Microsoft checks each version before it goes out, so it reaches you once it passes, usually within a few days. If Microsoft is still checking the last one, the new version waits for the next Friday.
- The Store page's **What's new in this version** lists, in plain words, everything that changed since the version you had from the Store, newest first.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.137

**Heiward from the Microsoft Store leaves the Heiward that Castellan keeps alone.**

### What changed

- If Castellan already keeps Heiward on your PC, Heiward from the Microsoft Store no longer offers to remove that copy, and doesn't set itself up beside it. Its page says Castellan already keeps Heiward on this PC and up to date, has an **Open Heiward** button that goes to Castellan's Heiward, and says you can uninstall the Store app in Settings > Apps > Installed apps.
- The Store version no longer stops Castellan's Heiward to show its own page, and its scheduled scans never replace Castellan's. Only one copy of Heiward runs on a PC.
- A copy from GitHub that Castellan doesn't look after is replaced as before, with the same checked box in the Store version's setup.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.136

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.135

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.134

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.133

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.132

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.131

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.130

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.129

**Checkboxes are checked now, not ticked.**

### What changed

- The page, its tour and help say "check": the copies it checks for you, **Check the files to remove**, and "Nothing is removed until you check it". The tag compare, in the desktop and web apps, says **Uncheck "Only show differences"**.
- Where the page meant its daily look, it says so: "Not looked at yet", "looked at 5 min ago", "right after the daily developer look".

### Before you update

- Nothing: it updates itself as usual.

## 1.9.128

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.127

**Heiward's page, laid out for a wide screen.**

### What's new

- Tiles at the top: the free space on your drives, the copies to review and the space they take, the developer files and games when those modes are on, and what you've freed so far. Click one to open its tab.
- Tabs for **Drives**, **Duplicates**, **Developer**, **Games** and **History**, at every width. On a wide screen each shows two parts side by side: the drive cards beside **At a glance**, the photos and videos by type beside where the duplicates are, the Developer and Games cards beside what they found, kind by kind.
- **History** shows what you've freed: in all, in the last 30 days, and a bar a week for the last 12 weeks, copies and developer files each in their colour, beside the list.

### What changed

- On a very wide screen the page stops at 1680 pixels wide, centred, instead of spreading into three columns.
- The History list shows the newest 25 at first, one line each; **Show all** lists the rest.
- **At a glance**'s "Worth knowing" links straight to the Duplicates, Developer and Games tabs.
- The tour opens each tab as it reaches it.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.126

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.125

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.124

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.123

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.122

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.121

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.120

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.119

**Brings 6 changes: The Games list no longer greys out games; Your games with their covers, in a grid; The Developer area no longer greys out repositories; Uninstall a game from its page, with its launcher's own uninstaller; Castellan's home page can list each of your drives; Heiward's home page uses the room a wide screen gives, rather than one long column.**

### What's new

- The Games area's Overview shows your biggest games as a grid of covers, like a launcher's library. **Show all** grows it to every game.
- The covers are the pictures your launchers already keep on your PC: Steam's library art, or your own custom art in Steam, and an Xbox game's logo. Heiward downloads nothing for them.
- A game without a cover shows a tile with its name and launcher instead.
- The Games list has a small cover beside each game, and a game's own page shows its cover beside its name.
- Each game's page has an **Uninstall** button beside its cover and name. It opens the uninstaller of the launcher the game came from, which asks before it removes anything. Heiward itself still deletes nothing: the next games check shows whether the game is gone.
- **Steam:** Steam asks in its own window.
- **GOG GALAXY, the EA app, Ubisoft Connect and Battle.net:** Heiward first shows the game, its size, launcher and folder, and opens the game's uninstaller only when you press **Uninstall** there. **Cancel**, or Esc, closes it with nothing opened.
- **Xbox and Microsoft Store games:** Windows' Installed apps opens, to find the game there.
- **Epic Games**, which doesn't let other apps uninstall its games, and any game whose uninstaller Windows doesn't list: the button is greyed, and pointing at it says where to uninstall it instead.

### What changed

- Every game in the Games list shows at full strength. A game with nothing of its own to clean up used to have its name greyed, which looked as if it were turned off. Its own page still says when there's nothing of it to clean up.
- Cleaner game pages: more space, and when you last played is said once.
- "Played … months ago" now counts the same way everywhere, so a game's page and its list never give different months.
- Every repository in the Developer area's list shows at full strength. One with no build outputs, merged branches or open pull requests used to have its name greyed, which looked as if it were turned off. Its own page still says when there's nothing in it to clean.
- The Games area's advice no longer says Heiward doesn't uninstall games: it says Heiward never deletes a game itself, and that a game's page can open its launcher's uninstaller.
- Heiward now tells Castellan each drive's name, size and free space, as well as the totals it already sent. Castellan's **Disk space** uses them to list your drives one by one, with a bar for each, under the summary of all of them together.
- Heiward's own page is unchanged.
- **On a 1080p screen** (and any window from 1200 px wide) the scan under way and your totals run across the top, with your drives on the left and the Developer area, the Games, where the duplicates are and the History beside them. Before, a window under 1700 px wide showed one column.
- **On a 2K or 3K screen** the home page has three columns: your drives, then the Developer and Games areas, then where the duplicates are and the History. With developer and game mode off, where the duplicates are sits beside your drives.
- **On a phone, or in a narrow window**, the page is one column, in the same order as before.
- The Developer and Games cards now fill their column, as the list of folders below them does.

### Before you update

- Nothing: it updates itself as usual. Castellan's list of drives needs this Heiward; until it has it, Castellan shows just the summary.

## 1.9.118

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.117

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.116

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.115

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.114

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.113

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.112

**Worktrees Reeve keeps because they hold your work are yours again, with Reeve's reason beside each.**

### What's new

- At a manor with Reeve, a worktree Reeve's worktree-tidy job keeps for a reason only you can sort out is no longer locked as "Reeve looks after it". It's tagged with Reeve's reason, such as "Reeve keeps it: has changes that aren't committed" or "Reeve keeps it: a git merge is under way in it", and says how long it has gone untouched. The reasons are changes that aren't committed, commits only on this PC, nothing on origin to check the work against, git-ignored files that aren't build output, and a merge or rebase under way.
- You can tick such a worktree to remove it. It's never ticked for you, Select all leaves it out, and automatic cleanup never takes it. Removing it asks first and says what goes with it. Its build output is deleted, and the rest goes to the Recycle Bin with git's record of the worktree, so you can put them back. Its branch and its commits stay in the repository. If the Recycle Bin can't take it, nothing is removed but its build output.

### What changed

- Every other worktree Reeve's tidy job goes through is still tagged "Reeve looks after it", and Reeve removes it once it's merged and clean.

### Before you update

- Update Reeve too. Until it says which worktrees it keeps for you, those stay tagged "Reeve looks after it", as before.

## 1.9.111

**The Developer area shows the worktrees on every drive Heiward checks, and tags only those Reeve really looks after.**

### What's new

- Worktrees on a drive Heiward checks are listed even when their repository is on a drive scanned only when you ask (a worktree in E:\worktrees of a clone on D:\, say). They're measured without scanning that drive (only git's one-line record of each worktree is read there) and never ticked for you. When you clean one, Heiward checks it with git first, as it does for any worktree.

### What changed

- At a manor with Reeve, only the worktrees Reeve's worktree-tidy job goes through are tagged "Reeve looks after it". Any other worktree is yours: shown untagged, never ticked for you, and cleaned when you tick it, with Heiward's usual checks (nothing uncommitted, nothing unpushed, nothing working in it).
- Each worktree is shown by the drive it's really on (E:\worktrees\x, not C:\worktrees\x through a link), as Reeve shows it.
- A worktree that has been deleted from a drive Heiward checks no longer stays listed from an earlier check.

### Before you update

- Update Reeve too. Until it says which worktrees its tidy job goes through, Heiward tags them all "Reeve looks after it", as before.
- The next developer check lists the worktrees it now finds, so it can take longer than usual the first time.

## 1.9.110

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.109

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.108

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.107

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.106

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.105

**An update says it's an update: "Updated from 1.9.104 to 1.9.105", not "Installed".**

### What changed

- When Heiward replaces a copy that's already installed (from Manor, its own updates or a setup run again), its last words say so: "Updated from 1.9.104 to 1.9.105. A scan starts now; it only looks at new files." Before, every update ended with "Installed. The first scan starts now.", which Manor's Updates box showed as if Heiward were new.
- Where the old version isn't known (the Microsoft Store version), it says "Updated to 1.9.105". The same version set up again says "Reinstalled". A new install keeps its words.
- Only the words changed: what an install or an update starts is as before.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.104

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.103

**Heiward's first scan in a manor speaks like the rest of the staff: a newcomer waits their turn at the door, then learns the lay of the land.**

### What changed

- While Heiward's first scan waits for the other agents' first rounds, the page says: "Heiward is new to the manor and waits their turn at the door: newcomers do their first rounds one at a time, so this PC is never swamped. They start as soon as the one before them has finished."
- While it runs: "Heiward is learning the lay of the land in their first scan: scanning every drive for the first time…".

### Before you update

- Nothing: it updates itself as usual.

## 1.9.102

**Background scans do far less work: TypeScript files are no longer checked as videos, the list of scanned files keeps only what's in the folders Heiward scans and is saved only when something changed, and the logs stop growing.**

### What changed

- Files ending in `.ts` that are TypeScript source code, not video recordings, are recognized from their first bytes. Before, every scan opened each of them with FFmpeg's ffprobe, one program start per file, and counted them among your photos and videos. Now they're left alone, and not counted.
- A file ffprobe found no video in isn't checked again until it changes. Files that failed in earlier versions get one more look on the first scan, then are remembered.
- The list of scanned files now holds only the files in the folders Heiward scans. It used to keep every file it had ever seen, including ones since deleted and ones in folders scans now leave out, such as developer repositories: on one PC 307,700 files, of which 5,245 were still scanned. A drive Heiward scans only when you ask, and a drive that isn't plugged in, keep their files.
- The list is saved only when a scan changed something, instead of written in full twice per scan. The AI's stored image fingerprints of files no longer in the list are dropped with them.
- Heiward's logs, `heiward.log` and the `log.txt` beside the program, no longer grow without end: once one reaches 1 MB it's kept as `heiward.1.log` or `log.1.txt` and a new one starts. A log that grew large in earlier versions is cut to its last megabyte.
- On that PC, a background scan went from 27 seconds to 6, from 2 GB of memory to 0.2 GB, and from writing 962 MB to the disk to writing nothing, and found the same duplicates.

### Before you update

- Nothing: it updates itself as usual. The first scan after the update tidies the list of scanned files once, and uses no more memory than scans did before; the scans after it use far less.
- If you later include a folder that scans leave out now, its photos and videos are read on the next scan, as new files are.

## 1.9.101

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.100

**The Microsoft Store version carries the same signed FFmpeg as the download, nothing downloads FFmpeg without a pinned hash, and the switch to a renewed signing certificate is planned so that updates never stop.**

### What changed

- **Microsoft Store version:** the FFmpeg inside it, which reads your videos and photos, is now the same build the GitHub version downloads (FFmpeg 8.1.3, lean LGPL build `n8.1.3-6`), with every program and library in it signed by Heiward's publisher. Before, the Store package carried an earlier, unsigned build of it (`n8.1.3-4`). The package build now stops if the FFmpeg archive doesn't have its pinned SHA-256, or if any of its files isn't signed by Heiward's publisher.
- Heiward runs on Windows only, and only Windows downloads FFmpeg: the builds Heiward pins, each checked against its SHA-256 before it's unpacked. The apps Heiward inherited from Video Duplicate Finder no longer download FFmpeg's unpinned daily build on Linux, or a macOS build that no longer exists; there they ask you to install FFmpeg from your package manager, as they did when a download failed.
- Heiward's releases are signed by a certificate that expires on 2027-10-11. Each release now records when every certificate it trusts expires. When the certificate is renewed, a release that trusts both the old and the new one comes out first, signed by the old one, and only after that are releases signed by the new one, so your copy keeps updating itself. Releasing checks that this order is kept.

### Before you update

- Nothing: it updates itself as usual. The Microsoft Store version gets the signed FFmpeg with its next Store update.

## 1.9.99

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.98

**Updates install only when they're signed by Heiward's publisher, every download is checked against a pinned SHA-256, and Heiward carries its license and third-party notices.**

### What's new

- **About** links the full text of Heiward's license, the GNU AGPL v3, and the third-party notices: the work of others Heiward includes or downloads (FFmpeg, ONNX Runtime, the .NET runtime and packages, the AI models and the NPU and graphics card packs), each with its license. Both are installed beside `hei.exe` as `LICENSE.txt` and `THIRD-PARTY-NOTICES.txt`, and come with each release.

### What changed

- Before installing an update from GitHub, Heiward now also checks its signature: Windows must trust it, it must be timestamped, and it must be signed by Heiward's publisher. One that isn't is deleted, the log and Settings' **Updates** say why, and the copy you have keeps working.
- On Intel and AMD PCs, FFmpeg now downloads only the signed build Heiward pins, checked against its SHA-256, as on Arm PCs. Before, should that download fail, Heiward took FFmpeg's newest daily build instead, which Smart App Control blocks and no hash checks.
- ONNX Runtime, which runs the AI, is now checked against its pinned SHA-256 when it downloads. A download that doesn't match is deleted, and the runtime you have stays in place.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.97

**Temp's leftovers, and Claude Code's ended sessions' temp files, go after a day untouched; a folder a program still holds stays whole.**

### What's new

- **Temp files and crash dumps** now takes what has sat untouched in your Temp folder for a day, not a week, and Claude Code's ended sessions' temp files (their scratchpads and task output) once they've been idle a day. A session that's running, was resumed that day, or is still open in the Claude app keeps its folder, as before. On a developer's PC, tests' leftovers and ended sessions' folders can fill a drive within days.
- After automatic cleanup deletes developer leftovers, the card's **Last run**, the notification and `heiward.log` say each drive's free space before and after, and once more at a run at least 10 minutes later. On a Dev Drive (ReFS) what a drive gets back can differ from the files' sizes: a file cloned from another frees nothing until the other goes too, and ReFS gives deleted space back a while after.
- History says what went: "Temp folder (1,204 items untouched for a day)", "Claude Code sessions' temp files (37 ended sessions' folders and 12 other items)".

### What changed

- A folder in Temp that a program still holds (a file in it open, or a program working in it: an installer waiting for a restart, an app running from what it unpacked) now goes whole or not at all. It's renamed away before it's deleted, which Windows refuses while it's in use, and it's looked at again the next time. Before, the files in it that weren't open went, which could break that program. This holds for the Developer area's **Clean** button too, which now says how many such folders it left.
- A folder in Temp part of which can't be read is left alone.
- What tools keep in Temp on purpose stays, however old: Node.js's compile cache, what .NET single-file apps unpack and the .NET SDK's builds of file-based apps, WinGet's and Chocolatey's downloads, NVIDIA's shader cache with older drivers, the Low folder sandboxed programs use, and .NET tools' first-run markers.
- A junction or symbolic link in Temp is judged by its own age and removed as a link; what it points to is never touched. Before, it was never cleaned.
- The setting is now `tempUntouchedDays` (a day, and never less) in place of `tempOlderThanDays` (7 days).

### Before you update

- Automatic cleanup stays as you set it. Developer leftovers are cleaned automatically only with that switch on in Settings' **Automatic cleanup** (in developer mode), and Temp's only with **Temp files and crash dumps** picked there. Otherwise the Developer area lists them for you to clean.
- If you set `tempOlderThanDays` in `settings.json` yourself, set `tempUntouchedDays` instead: the old setting is no longer read.

## 1.9.96

**The developer check is several times faster on a busy Temp folder.**

### What changed

- The developer check measures your Temp folder several items at once instead of one at a time. On a developer's PC Temp can hold millions of files, and measuring it was most of the check: one that took about three minutes spent nearly all of them there. On that PC, measuring Temp went from 10–30 seconds to under three with the files already cached, and by more when they aren't. It finds exactly what it found before, in the same order.
- Stop scan stops the developer check while it measures Temp, too.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.95

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.93

**Cleaning developer files is several times faster, the Temp folder most of all.**

### What changed

- Cleaning the Temp folder no longer looks through all of it again first. Before deleting, it looked at every file in Temp once more, including the ones it wasn't going to delete. On a developer's PC that can be millions of files, which took minutes before anything was deleted. Now it looks again only at what it listed, each item just before it goes, so something used since the list was made is still left alone.
- Developer files are deleted several folders at once instead of one file at a time. A `node_modules` of 100,000 files took about five seconds and now takes about one.
- Cleaning a git worktree deletes its build output (`node_modules`, `bin`, `obj` and the like, which git ignores) several folders at once before git removes the rest. Git deletes one file at a time, so this part used to take longest. Your files are checked exactly as before.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.92

**The end of a scan is quick even with thousands of look-alikes, Stop scan works until the very end, and the page says what the scan is finishing.**

### What changed

- Making the report is many times faster on big sets of look-alikes. Each photo's capture date is read once instead of once for every file in its set, and each name is checked once for a burst. A set of 2,000 look-alike screenshots took a minute and now takes under two seconds; one of 15,000, which took hours, now takes about two minutes.
- Stop scan works while the report is made and while the developer and games checks that follow it run. Before, once the comparing was done, Stop went unheard until the scan ended by itself, however long that took. Stopped while the report is made, the last report stays as it was; stopped after it, the report is kept and the checks and automatic cleanup wait for the next scan.
- The last step's status says what it's doing: making the report (with how many of the sets' files are done, and a bar), saving it, checking your developer files or games, or cleaning up automatically. It used to say "checking the copies byte for byte" throughout.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.91

**Scans leave your Temp folder out wherever it is, and a file that has gone no longer stops every scan.**

### What changed

- Your Temp folder is left out of scans wherever it is. It was left out only in its usual place, under your account's app data: once moved, to a Dev Drive say, its throwaway pictures were listed and compared like your photos, every one against every other, which could keep a scan busy for hours. It's left out by the path Windows gives it and, through a junction or a mounted folder, by where it really is. A Temp folder set to a drive's root, your profile or your Pictures is never left out.
- A result whose file had gone since it was found (a temp file, or one on a drive scanned only when you ask) stopped the whole scan while it read the tracks' languages, so scans ended before saving anything, every time. Such a file is now passed over.

### Before you update

- Nothing: it updates itself as usual. Its next scan no longer lists what's in a moved Temp folder, so sets of copies found only there drop off the page.

## 1.9.90

**The developer check leaves a drive scanned only when you ask alone, and finds the caches you moved to a Dev Drive.**

### What's new

- On a drive set to Scan only when I ask, the developer check reads nothing: projects, worktrees, package caches, emulators, temp files and crash dumps there aren't looked for, measured or asked of git, even when you reach them through a junction or a folder the drive is mounted in. What the last check found there stays listed as it was, with a line saying which drive it's on and when it was measured.
- Scan this drive now, on such a drive's page, checks its developer items too, so they're up to date.
- Package caches are found where their tools' settings put them, as Windows' Dev Drive guidance moves them: npm's cache in .npmrc, pnpm's store-dir, NUGET_PACKAGES and NuGet's other caches, PIP_CACHE_DIR, GRADLE_USER_HOME, CARGO_HOME, GOCACHE, YARN_CACHE_FOLDER and Maven's maven.repo.local. The folder a tool used before is listed too, ticked: nothing uses it any more.

### What changed

- Automatic cleanup leaves developer items and merged branches on a drive scanned only when you ask for you, as it does duplicate sets there ("It's on a drive scanned only when you ask, so it waits for you"). Clean still works on them when you ask, checking each again first.
- A worktree on such a drive keeps its branch counted as checked out, so pruning merged branches never takes it.

### Before you update

- Nothing: it updates itself as usual. If you want the developer check to keep cleaning up a drive you set to Scan only when I ask (a Dev Drive holding your temp files, say), right-click that drive under This PC and choose Scan automatically again.

## 1.9.89

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.88

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.87

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.85

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.84

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.83

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.81

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.77

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.76

**Claude Code's old temp files go one session at a time, and never those of a session still in use.**

### What's new

- Developer mode's **Temp files and crash dumps** has a new row, **Claude Code sessions' temp files**: the folder Claude Code keeps in your Temp folder for each session (its scratchpad, its tasks' output), counted and measured a session at a time. A session's folder is ticked once nothing in it has changed for 7 days (`tempOlderThanDays`), and automatic cleanup takes it with your other temp files.
- Never one a session may still use, whatever its age: a session that's running, one you can still resume (it was used in those 7 days), one the Claude app lists as open rather than archived, or a folder a program has a file open in. Each is asked again just before anything is deleted.

### What changed

- Claude Code's temp folder no longer counts as a single Temp entry. Live sessions write in it every minute, so as a whole it was never old enough to go, and it could grow to tens of gigabytes. The rest of what's in it is judged as your other temp files are, and a project's folder goes once its sessions' have.

### Before you update

- Nothing: it updates itself as usual. The row appears after the next developer check.

## 1.9.75

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.74

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.73

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.72

**Each Windows account on a PC has its own Heiward tasks, so two accounts never share or fight over one.**

### What changed

- Heiward's scheduled scan and its other tasks are now in a Task Scheduler folder of each account's own, named after the account: `\Heiward (<account>)\Scan`. Before, every account's were in one `Heiward` folder, where the names belonged to whichever account installed Heiward first. This is how Castellan names each of its programs' tasks when it's installed for everyone on a PC.
- When Heiward updates, it moves your account's tasks from their old names to the new folder. It moves only your own: another account's Heiward keeps its tasks as they are. The Microsoft Store version moves them the first time its page starts after the update, and your scans run as scheduled in between.
- Uninstalling removes your account's tasks under both the new names and the old ones, and never another account's.

### Before you update

- Nothing: it updates itself as usual.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.70

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.69

**Urgent fix: Heiward installs on a PC where another account installed it first, and its FFmpeg runs where Smart App Control is on.**

### What changed

- On a PC where another Windows account had installed Heiward first, the install stopped at "Task Scheduler refused 'Heiward\Scan': Access is denied", and Heiward had no shortcut. Task Scheduler's names are the PC's, not each account's, so the other account's scan task held the name. Heiward now puts this account's tasks in a folder of their own inside Heiward's, named after your account, and finds them there from then on.
- Heiward reads your videos with FFmpeg, which it downloads when it installs. The builds it downloaded weren't signed, and Smart App Control blocks a program no one signed that it doesn't know: where it's on, videos couldn't be scanned, with a Smart App Control notice for each try. Heiward now downloads the manor's FFmpeg, whose every program and library is signed, on both x64 and ARM64 PCs.
- Where Smart App Control is on, an unsigned FFmpeg Heiward downloaded before is replaced with the signed one when Heiward updates.

### Before you update

Nothing: it updates itself as usual.

## 1.9.68

**The Developer area no longer lists repositories that tests left in your Temp folder.**

### What changed

- The Developer area leaves out repositories inside a Temp folder: your account's Temp, and a Temp or Tmp folder at the root of a drive. Tests and tools make throwaway repositories there by the thousand and leave them behind; they buried your own projects in the list. Their space is still offered for cleanup under Temporary files once they're old enough.

### Before you update

Nothing: it updates itself as usual.

## 1.9.67

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.65

**The tour holds the page until you answer it or skip it.**

### What changed

- While Heiward's tour shows, the page under it is dimmed and can't be clicked, as Castellan's own welcome does: each step is answered, or the tour skipped, before you go on. Walking the page's parts, the card sits in the corner and the page is only lightly dimmed, so the part it names stays easy to see. Escape still skips it.

### Before you update

Nothing: it updates itself as usual.

## 1.9.64

**A new install waits for you before its first scan.**

### What's new

- The tour's last step asks when the first scan should run: **Scan now**, **Scan by itself** (it runs with the next scheduled scan, at the pace you chose), or **Later**.
- Until you choose, a banner on Heiward's page asks the same. At a manor, Manor's page lists "Heiward's first scan waits for you".

### What changed

- Installing Heiward no longer starts a first scan, at full speed, the moment setup finishes. It scans nothing until you have opened its page, seen the tour, and pressed Scan now or chosen Scan by itself. Scheduled scans skip themselves until then.
- The Store version's setup button now reads **Set up**: the first scan waits for your choice there too.

### Before you update

Nothing: it updates itself as usual. An update scans as before: only a new install waits.

## 1.9.63

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.62

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.61

**A drive written in settings.json by its letter alone, such as "D:", now means the whole drive.**

### What changed

- A folder written in settings.json as a drive letter alone, such as `"folders": ["D:"]`, is now scanned as that whole drive, the same as `"D:\\"`. Windows reads "D:" as the folder Heiward's process is working in on that drive, which on the drive it was started from is rarely the root. A scan could then look at one folder of the drive instead of all of it. Heiward's own page always saves a drive with its backslash, so only a hand-edited settings.json was affected.
- The same applies wherever Heiward checks which drive a folder is really on, for a drive mounted in a folder or a junction. No part of Heiward asks that about a bare drive letter today, but a test that did failed on a PC with a drive mounted in a folder.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.60

**Manor's disk tile asks Heiward for the drives at a glance alone, not for everything the review page shows.**

### What changed

- Heiward's page has a new `GET /api/glance`, which answers with the home page's "At a glance" alone: the drives' size and free space, the used space by kind, and what could be reclaimed. Manor's disk tile asks for it once a minute. It used to ask for `/api/state`, everything the review page draws: the review lists, the History, the settings, the graphics cards and the schedule. On this PC that was 77 KB a minute, of which Manor kept 1 KB. The glance reads only the report, the index, your decisions, and the developer and games reports. It takes about half the processor time per request that `/api/state` does: 7 million cycles against 13 million, measured over 300 requests in a row.
- The Recycle Bin's size is read at most every 5 minutes, and again as soon as Heiward moves files there. It used to be read once a minute, so Manor's tile, asking once a minute too, read it every time.
- The graphics cards are looked up at most every 5 minutes instead of every minute, for the review page's settings.

### Before you update

- Nothing: it updates itself as usual. Manor uses the glance from its next version on, and asks an older Heiward for `/api/state` as before.

## 1.9.59

**Scans leave package caches out, list a folder that was added, moved or deleted by itself instead of walking the whole drive, and keep up with a busy drive's journal. The review page uses less of the processor while it waits.**

### What changed

- Package caches moved out of their usual places are left out of scans, shown as "Package cache" on the review page. By default npm's, NuGet's, pip's, Cargo's and other package managers' caches are dot-folders or live in AppData, and scans already leave those out. A Dev Drive set up by Microsoft's guide moves them to plain folders (D:\packages\nuget and the like), named by variables such as NUGET_PACKAGES and npm_config_cache. Scans walked those folders and listed the packages' icons as duplicates of each other: on one PC, 76 of the report's 94 paths. Heiward now reads those variables (and Maven's -Dmaven.repo.local in MAVEN_OPTS) and leaves the folders out. It ignores a variable that names your profile or one of your own folders.
- A folder added, moved in or made visible where scans look is now listed by itself, with what's below it. Before, one new folder anywhere a scan looks (a clone, an unpacked download, a build's output) made the next scan walk the whole drive, 58 to 96 s on C:\. A folder that is deleted, moved away, or made a code repository, hidden or a link now drops its photos and videos from the listing. A folder created empty changes nothing, so a scheduled scan still doesn't run.
- The review page now checks each drive's change journal every ten minutes. Once a quarter of the journal holds changes the next scan hasn't read, the page reads them and keeps what changed where scans look, and where it read up to. The journal on C:\ holds 32 MB, about 80 minutes of a busy afternoon. Scans an hour apart found it overwritten five times on one day, and each of those scans walked all of C:\. A scan now reads only what came after the page's last look. A look at a full journal takes about a second at low priority, and a quiet drive's journal isn't read at all. The page writes nothing while a scan runs, and never over a listing a scan saved since it looked.
- Placing an hour's changes takes half the time it did: most of the folders they happened in are a build's or an installer's, made and deleted within the hour, and Heiward no longer asks Windows where a folder the journal saw deleted is.
- A folder deleted elsewhere on the drive while a scan placed its changes no longer makes that scan walk the whole drive.
- The log now tells an overwritten journal from one Windows wouldn't read ("the journal couldn't be read (Windows error 87)"). Before, both read "the journal no longer reaches back to the last scan".
- A walk lists each folder once, and gets its photos and videos, its subfolders, and whether it's a code repository from that one listing. It used to list each folder twice and ask every subfolder about a dozen repository markers, one at a time: about 85,000 lookups for a folder of some 7,000 folders. Walking that folder, package caches and all, went from a median of 1.8 to 2.0 s down to 0.2 to 0.4 s.
- The review page's process uses less of the processor while it waits. It took about 3.4 s an hour while idle, three-quarters of that measuring the tool caches' sizes every hour (71,000 files in 13,400 folders). It now measures them once a day, and gives each drive's free space as Windows reports it when a program asks.
- The review page's ping now says whether Heiward is on duty, since when it has been paused, and its one-line status, as `hei status --json` does. Manor used to run `hei status` every two minutes to learn them, starting Heiward and Task Scheduler each time: about 20 to 30 s of processor time an hour. With them in the ping, Manor doesn't need to.
- The page now asks Task Scheduler for the next scan every half hour, or once that scan's time has come, instead of every minute. It counts the sets waiting for review again only when the report, your decisions, automatic cleanup or your settings change, or every half hour.

### Before you update

- Nothing: it updates itself as usual. The first scan after the update walks each drive once, because Heiward lists differently and the new exclusions are part of the listing's key. Later scans go by the drive's journal again.

## 1.9.58

**The badge says "Running on the NPU" only while a scan is using the NPU.**

### What changed

- The badge at the top of the page read "Running on the NPU" whenever the last scan had used the NPU: through every minute of a long scan, and after it ended. A scan uses the NPU only while it matches pictures and video frames. It spends most of its time listing, reading and comparing files, and making the report, and the NPU rests then. The badge now says "Running on the NPU" only within seconds of the NPU working for the scan, as Manor's Accelerators card does, and "NPU ready" the rest of the time.
- The badge's tip says which it is: the NPU matching pictures for this scan now, set up for this scan and used only while it matches pictures, or the NPU that ran the last scan.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.57

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.56

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.55

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.54

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.53

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.52

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.51

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.50

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.49

**Heiward's notifications say Heiward, with its icon, however it was installed.**

### What changed

- Heiward's notifications (new sets of likely duplicates, an automatic cleanup) came under **Windows PowerShell** unless Heiward's own setup had installed it, for example when Castellan installed it. Now Heiward makes its name known to Windows, with its icon, the first time it notifies, wherever it came from, and the icon is on each notification too. In Windows' notification settings you can turn Heiward's notifications on or off without touching any other program's.

### Before you update

Nothing: it updates itself as usual. The first notification after the update makes Heiward's name known to Windows.

## 1.9.48

**A release that can't sign because SimplySign's key provider is missing now says what to do.**

### What changed

- When SimplySign Desktop is running but its key provider isn't there ("The specified CSP could not be found"), building a release now stops at once and says to disconnect and connect SimplySign Desktop again, instead of showing signtool's own message. The failure isn't retried.
- This release also brings 1.9.47, which wasn't released on its own.

### Before you update

Nothing: it updates itself as usual.

## Unreleased

**An NPU whose plugin can't set the AI model up no longer ends Heiward's scan: the NPU is checked in a process of its own first, and a scan leaves an NPU that failed its check. An AMD Ryzen AI NPU runs the model. AI matching on the processor works again on PCs where Windows has its own ONNX Runtime.**

### What changed

- Before a scan first uses the NPU with its driver and plugin, Heiward checks it in its own process (`hei probe --device npu`), as it already did for a graphics card. On a Ryzen AI 7 350 (NPU driver 32.0.20102.3930), Windows ML's Vitis AI plugin 1.8.75.0 ended Heiward's process from native code while it compiled the AI model, past Heiward's own fallback to the processor, so the first scan never finished, tried again every hour, and left the NPU's lock held by a process that had ended (Castellan showed the NPU in use by Heiward). Now a check that ends that way is recorded in `ai\npu-checks.json`: scans leave the NPU, for the graphics card once it has passed its check or else the processor, until the NPU's driver, its plugin or Heiward's NPU pack changes, or for a day. (Windows ML tells a process the Vitis AI plugin's version only once the plugin is added to that process, so Heiward adds it before asking. Otherwise the scan and its check saw different versions, and the NPU would have been checked again before every scan.) The check's half-compiled model is deleted, and the NPU's lock it died holding is let go.
- On that Ryzen AI 7 350, the NPU now runs the AI model. The Vitis AI plugin's compiler loads the plugin's own DLL by its short (8.3) path, which Windows can't work out for a standard user under `C:\Program Files\WindowsApps`, so every compile failed and the plugin ended the process, whatever the model. Heiward now loads the plugin through a junction to its folder, `ai\vitisai-plugin`, made without admin rights and pointed again when Windows updates the plugin. The NPU also gets one picture per run: with 8, only the first picture of each run came back right. Its embeddings match the processor's (cosine 0.998 or better), at about 8 ms a picture. A check that failed before this update doesn't hold the NPU back.
- AI matching on the processor ended Heiward's process (0xC0000005) when it was the first thing in that process to use ONNX Runtime, on a PC whose Windows has its own ONNX Runtime in System32 (version 1.17, from a Windows 11 update in October 2026). Heiward loaded that one instead of its own 1.27, and it can't serve Heiward. This is what an NPU that failed its check, or a PC without an NPU, falls back to. Heiward now always loads its own.
- A check that passes leaves the compiled model for the scan, which then loads it instead of compiling it.
- The check also makes sure the NPU's embeddings are right, as a plugin can run the model and still get it wrong without an error, as Vitis AI did at 8 pictures a run. It embeds a run's worth of test pictures and one more on the NPU and on the processor, with the processor running the same FP32 model, and compares each picture's pair. An NPU whose picture is under a cosine of 0.98 fails its check, as one that crashed does: scans leave it until its driver or Heiward's NPU pack changes, or for a day, and its compiled model is deleted. The check's reason says which pictures in a run came back wrong.
- The same check runs before `hei setup` opens the AI model, and before the review page's picture comparison first opens it. When setup copies the NPU pack from the installed Heiward and the plugin ends the copy's trial run, that counts as the check: the copy is kept and the model isn't compiled a second time.
- A check that gives no answer in 10 minutes is ended and counts as failed. On the same PC, Vitis AI's compile once hung instead, every thread waiting, which left the scan waiting for good.
- A probe's output is cut to its last 20 lines in the window and the log: Vitis AI's compiler writes about 800.
- The Microsoft Store version needs Windows 11, version 24H2 or later: Heiward runs on .NET 10, which Microsoft supports on Windows 11 from 24H2. The package's minimum Windows (`MinVersion` in `HEI.Agent/Store/AppxManifest.xml`) was Windows 10 version 2004, and `docs/STORE.md` gave that as the listing's requirement. The Store no longer offers Heiward to an older Windows, and a Store copy already on one gets no more updates from it.

### Before you update

Nothing: it updates itself as usual. The first scan after updating checks the NPU once, which takes as long as compiling the model: a few seconds on a Snapdragon or an Intel NPU, about a minute and a half on an AMD one.

## 1.9.47

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.46

**A scan starts half the processes it did, one for each new file instead of two, and on an Intel NPU it starts in under a second, not 15 to 18.**

### What changed

- The FFmpeg tools a scan runs for each new file (ffprobe, and ffmpeg or fpcalc when they're run as programs) share Heiward's own hidden console instead of each getting a console host (conhost.exe) of its own: 20 files probed started 41 processes, now 21, and 50 probes take 0.5 s instead of 1.0. The same goes for the git, schtasks, gh and FFmpeg checks Heiward runs itself. The desktop app, which has no console to share, starts them as before.
- git now starts the real git.exe behind Git for Windows' launcher (`Git\cmd\git.exe`), which did nothing but start it: the developer check's git commands were three processes each, and are now one.
- On Intel AI Boost, the compiled AI model is now saved in Heiward's own folder (`ai\openvino-cache-1.7.0`). Intel's OpenVINO 1.7.0 ignored the setting Heiward used, so the folder stayed empty, and every scan compiled the model again unless the NPU driver happened to still hold its own copy. On a Core Ultra 5 225U, opening the NPU took 15–18 s to compile and 0.8 s from the saved one, with the same results.

### Before you update

Nothing: it updates itself as usual. The first scan on the NPU after updating compiles the model once more.

## 1.9.45

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.44

**A folder is known by where it really is, whatever path leads to it: through a folder link, or a drive mounted in a folder.**

### What changed

- A worktree in use is never offered for cleanup when it's reached through a folder link (C:\worktrees pointing to another drive, say): a Claude Code session that worked in it, or a program working in it, keeps it, whichever path they used.
- Worktrees a tool keeps in its own folder, and folders a scheduled task uses, are recognized by where they really are, so they aren't offered either.
- A folder reached two ways, such as a folder where a Dev Drive is mounted and the drive's own letter, is scanned once, so a photo is never listed twice as two copies of itself. A drive mounted only in a folder, with no letter, is scanned too when every drive is.
- Changes since the last scan are read from the drive a folder is really on. A Dev Drive keeps no change journal, so its folders are walked instead of being read from the wrong drive's journal.
- Folders you list, leave out or keep for scanning on request are matched by where they really are, so a path through a link or a mounted drive counts the same as the drive's own.

### Before you update

- The first scan after updating may walk folders on a drive mounted in a folder, or listed through a link, once more in full: they're now listed by where they really are (a folder by the drive mounted there, say). Scans after it are as quick as before.

## 1.9.43

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.42

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.41

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.40

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.39

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.38

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.36

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.35

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.34

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.33

**Releases are checked for a signing connection before they build. This release also brings 1.9.31 and 1.9.32, which weren't released on their own.**

### What changed

- Building a release now checks that the code-signing service is connected before it starts, and if it isn't, says to connect it. Before, the build failed partway through with signtool's "No certificates were found", after trying the same signature three times.

### Before you update

Nothing: it updates itself as usual.

## 1.9.32

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.31

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.30

**A fix to Heiward's own tests: nothing changes for you.**

### What changed

- One of Heiward's tests, the one that checks two holders of a graphics card take turns, failed now and then on a busy PC. It read a waiting turn's file while that file was still being written. It now waits until the file is written, as the other programs in line do. Heiward itself is unchanged.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.29

**Heiward starts with Windows the usual way: you'll find it in Startup apps, and it opens in its own window.**

### What's new

- Heiward is listed in Windows' Startup apps (Settings > Apps > Startup, and Task Manager's Startup apps), as Heiward. Turn it off there and it stays off, even after updates. Its page then starts when you open Heiward instead of when you sign in.

### What changed

- When duplicates wait for your review, Heiward's page opens in a window of its own (Microsoft Edge's, with no tabs or address bar), not in your default browser.
- With Castellan, Heiward no longer opens its page when you sign in at all. Castellan's page tells you when copies wait for your review, with a link to Heiward.

### Before you update

- Nothing: it updates itself as usual. The update moves Heiward from its hidden sign-in task to Startup apps.

## 1.9.28

**Deleting or linking duplicates from Heiward's page and desktop app never loses a file the Recycle Bin can't take.**

### What changed

- When you send duplicates to the Recycle Bin from Heiward's page or the desktop app, a file the bin can't take is left where it is, with the reason: one on a network or removable drive, on a drive set to delete files at once, too large for the bin, or one that would push the bin past its size (Windows would then delete what's already in it for good). Before, Windows deleted such files for good without asking.
- On Linux and macOS, a file that can't go to the trash is left where it is and you're told, instead of being deleted for good.
- Replacing a duplicate with a link to the copy you keep makes the link first and only then puts it in the duplicate's place, so a link that can't be made leaves the file as it was.

## 1.9.27

**Uninstalling Heiward looks like setting it up, and asks before it goes.**

### What's new

- Uninstalling Heiward from Settings > Apps opens a window like its setup: the same banner, steps and pictures. It says what goes and what stays, and asks whether to keep your settings and the report, for if you install Heiward again. Uninstall or Cancel, then it shows how it went.

### What changed

- Before, uninstalling from Settings > Apps happened silently, with no window. Tools that uninstall quietly, such as winget, still do, and so does Castellan's uninstall.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.26

**Heiward keeps more of your files out of harm's way when it cleans.**

### What changed

- A worktree is only removed when everything git ignores in it is build output a build makes again. One holding a `.env`, a local database or settings stays, and Heiward says which file kept it.
- What an unfinished worktree removal left behind now goes to the Recycle Bin, except the build output in it, so anything never committed can be put back.
- Build folders (`bin`, `obj`, `build`, `target`) are kept when git tracks files in them or they hold a database.
- Heiward never fills a drive's Recycle Bin past its size: Windows would then delete what's already in it for good. It asks you to empty the bin first. A folder with a path too long for the bin is left for you, too.
- More names count as saved games, and are never touched: folders and files named Save…, Slot… or Profile…, and Steam Cloud's `remote`.
- Heiward's page and pictures speak of Castellan's household staff, and show only the staff that use AI on the NPU.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.25

**Heiward's setup opens in a window of its own, and shows what Heiward does while it sets up.**

### What's new

- Run the downloaded Heiward and its setup opens in a window of its own, in place of a console window: a welcome that
  says what Heiward does, the few questions about your PC, then the setup's steps beside pictures of what Heiward does
  while it gets the AI ready. When it's done, **Open Heiward** turns the same window into Heiward's page.
- The window ends with Castellan, the staff of agents Heiward is part of, and a link to castellan-software.com.
- The Microsoft Store version's first start has the same look.

### What changed

- Started from a script or a command prompt, the setup still asks its questions there.
- If the window is closed before the setup starts, nothing is left running: run the download again to set Heiward up.

### Before you update

Nothing: it updates itself as usual.

## 1.9.24

**The home page says a word about Castellan, the household Heiward belongs to.**

### What's new

- Without Castellan installed, the home page ends with a short card on Castellan: the household of agents that look
  after your PC, Heiward among them, with a link to castellan-software.com. It's text in the page: nothing is fetched
  or sent for it unless you open the link. **Not now** hides it for good, and **The Castellan card** in Settings
  brings it back. With Castellan installed there's no card.

### What changed

- The privacy policy says so, and names Castellan where it said Manor.

### Before you update

- Nothing: it updates itself as usual. Heiward is free, and works the same with or without Castellan.

## 1.9.23

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.22

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.21

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.20

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.19

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.18

**A smoother first run, a page that fills a wide screen, lighter scan progress, and a quiet uninstall.**

### What's new

- In a wide window, such as a 2K or 4K screen, the home page uses two columns: the scan, the totals and your drives on the left; the Developer area, the Games, where the duplicates are and the History beside them. Narrower windows keep the single column.
- The Microsoft Store version's setup now also offers **Use more memory to scan faster** and **Keep a history**, the same switches as on the Settings page.

### What changed

- If you choose **Only when I press Scan now** during setup, no scan starts until you press Scan now. Before, the first scan started straight after setup anyway. The same goes for an update when scans run only on demand.
- The tour's first step describes when and how hard Heiward scans as your settings have it, rather than always saying it scans in the background.
- The Game mode and Developer mode switches in the tour now stay as you set them on the first click; before, the first click sprang back.
- While a scan runs, the page asks Heiward only for the scan's progress, every second and a half, and updates just the percent, the progress line and the bars, instead of reloading everything every two seconds. The page stops asking while it's hidden and catches up when you look again. On big libraries, both the page and the PC stay more responsive during a scan.
- Uninstalling from Settings > Apps no longer opens an empty console window. If something stops the uninstall, a message says what and where the details are.
- Links on the page use the theme's link colour, so they're easy to read in both light and dark themes.

### Before you update

Nothing: it updates itself as usual.

## 1.9.17

**Heiward's privacy policy now mentions its daily look for updates.**

### What changed

- The privacy policy says that the version from GitHub asks GitHub once a day for a newer Heiward, as it has done since it began updating itself. The Microsoft Store version leaves updates to the Store.
- Sharper logos for Heiward's Microsoft Store listing.

### Before you update

Nothing: it updates itself as usual.

## 1.9.16

**Heiward's downloads are now code-signed, so Windows names its publisher when you run them.**

### What's new

- Both downloads, for Intel/AMD and for Arm PCs, are signed by Jeremy Collier with a Certum code-signing certificate, and timestamped so the signature stays valid after the certificate expires. When you run one, Windows shows the publisher's name instead of "Unknown publisher".

### What changed

- Windows may still say "Windows protected your PC" right after a release, until enough people have run it. If it does, click **More info**, check the publisher is Jeremy Collier, then **Run anyway**.
- The Microsoft Store version is unchanged: Microsoft signs it, as before.

### Before you update

Nothing: it updates itself as usual.

## 1.9.15

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.14

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.13

**An About button beside Settings, as on Manor's page.**

### What changed

- About, beside Settings in the title bar, has what you read once: which version this is and where it came from, its license, and where its work runs. They were at the foot of Settings.
- Settings keeps every switch: Update by itself and the "In a manor" card are now under Updates.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.12

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.11

**The Store version of Heiward is built for every release, and sent to the Store once that is set up.**

### What changed

- Each release of Heiward now also builds its Microsoft Store package. Each one is submitted to the Store by itself, and Store installs update through Windows as before.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.10

**Heiward installed from GitHub keeps itself up to date.**

### What's new

- Once a day Heiward looks for a newer release. When there is one, it downloads it, checks it against the release's checksums and installs it, keeping your settings: where the AI runs, the schedule and the graphics card. It never updates during a scan.
- The About card has an **Update by itself** switch to turn this off, and says when Heiward last looked and what it found.
- `hei update` installs the newest release now (`hei update --check` only looks).

### What changed

- A copy that something else keeps up to date is left alone: the Store version, a Heiward that Manor employs, and development builds.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.9

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.8

**While Heiward scans, its page says plainly what it's doing and how far it has got.**

### What changed

- A scan under way now has its own panel at the top of the page: what Heiward is doing right now, how far through it is, and the scan's three steps (find photos and videos, check new and changed files, compare and make the report) with the ones done ticked.
- Once every drive's files are checked, the drive cards say so, instead of each showing the same comparing bar.
- The line at the top right says Scanning now while a scan runs.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.7

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.6

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.5

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.4

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.3

**Heiward's first scan waits its turn among the manor's newcomers, then runs at full speed.**

### What's new

- **Settling into the manor.** In a manor, Heiward's first scan, which reads every drive, no longer runs at the same time as the other agents' heavy first work. When everything is installed together, each agent does its first round in turn, so your PC isn't swamped. Heiward waits for the one before it, then scans at full speed while it has its turn, since nothing else heavy is running then. A game still makes it step aside. Every scan after that runs as you chose.
- While it waits, or while its first scan runs, the review page says so, with a small drawing of the manor's door in your theme's colours.
- Scan now never waits. Without a manor on this PC, or with Manor set to run everything at full speed, Heiward scans as before.

### Before you update

- Nothing: it updates itself as usual. A PC where Heiward has already scanned is never held up.

## 1.9.2

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.1

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.0

**Heiward lends Chamberlain its eye for pictures, so duplicate documents can be found.**

### What's new

- Chamberlain, finding documents that are copies of each other, can now ask Heiward whether two documents hold the same pictures. Two documents are copies only when they have the same words and the same pictures: when a picture was added, removed, swapped, cropped or retouched, they are different versions and are left as they are. Heiward compares the pictures by look, with the same rules it uses for your photos, so a picture that was only saved again or made smaller still counts as the same.
- It does this only when the PC can spare it: never while you play a game or use a full-screen program, or while Heiward is paused. During a scan, or without AI matching installed, it compares the pictures pixel by pixel alone, which gives the same answer. It reads no files for it and keeps nothing.
- Nothing changes on Heiward's own page.

### Before you update

- Nothing: it updates itself as usual.

## 1.8.0

**Game mode: Heiward looks after gamers' very large files, and its page speaks plainly to everyone who isn't a developer.**

### What's new

- **Game mode**, a new switch in Settings, off until you turn it on. Once a day Heiward looks at your games and lists them in a new **Games** area on the home page, by game, by drive and by kind, each with its size, what it is, and what removing it means:
  - your installed games from Steam, Epic Games, GOG GALAXY, the EA app, Ubisoft Connect, Battle.net and Xbox, with when you last played them where the launcher records it (Steam does);
  - leftovers of games you've uninstalled, and workshop downloads of games no longer installed;
  - the launchers' download caches;
  - shader caches: those of uninstalled games are ticked for you; those of games you have never are, as their first launch afterwards may stutter;
  - crash dumps and crash reports of your games;
  - the same game installed twice, and games you haven't played in months, biggest first, with how to move or uninstall each with its own launcher. Heiward doesn't move or uninstall games itself.
- Everything you remove there goes to the Recycle Bin. An installed game's own files and your saved games are never touched, nor anything of a launcher or a game while it's running.
- Automatic cleanup can take what games leave behind once you turn it on: crash dumps, download caches, and leftovers and shader caches of uninstalled games, to the Recycle Bin.
- At a glance on the home page has a **Games** slice.

### What changed

- With developer mode off, the page no longer shows technical details: no commands, settings-file or log names, model or runtime names, or the commit a build came from. The title bar's AI badge and Settings say the same things in plain words.
- With Manor installed and its Developer options off, Settings no longer shows the Developer mode card.

### Before you update

- Nothing: it updates itself as usual. Game mode stays off until you turn it on in Settings.

## 1.7.47

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.7.46

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.7.45

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.7.44

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.7.43

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.7.42

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.7.41

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.7.40

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.7.39

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.7.38

**A security fix: a folder name or notification text with curly quotes in it can no longer break out of Heiward's PowerShell commands.**

### What changed

- Windows PowerShell treats the curly quotes ‘ ’ ‚ ‛ as quote marks. Making Heiward's shortcut when it installs, and showing a Windows notification from the app, now escape every one of them, so a profile like `C:\Users\O’Brien` installs as it should and stays a name, never a command.

### Before you update

Nothing: it updates itself as usual.

## 1.7.37

**Heiward's own checks of developer mode no longer depend on what else is installed on the PC.**

### What changed

- The tests behind developer mode's worktree cleaning now run the same on a PC with Manor and Reeve installed as anywhere else, and the ones that watch a program working in a folder wait for it to have started. Nothing changes in what Heiward does.

### Before you update

Nothing: it updates itself as usual.

## 1.7.36

**The "In a manor" card is put away for now: it comes back when the manor is ready to join.**

### What changed

- The home page no longer ends with the "In a manor" card, and Settings no longer has its switch. Nothing else on the page changes, and Heiward works just as before.
- If you chose **Not now**, that choice is kept for when the card returns.

### Before you update

Nothing: it updates itself as usual.

## 1.7.35

**A short card at the foot of the home page about Manor, the household Heiward can join.**

### What's new

- Without Manor on the PC, the home page ends with an "In a manor" card: a few lines on the agents Heiward can work beside, all private and all on this PC, and a link to see the manor. It sits below everything else, never above your drives, and it's text in the page: nothing is fetched or sent for it.
- **Not now** hides it for good. To bring it back, turn on **The "In a manor" card** under About Heiward in Settings.
- With Manor installed there's no card: the title bar's **Back to** link already says where Heiward works.
- Heiward stays free and complete on its own: nothing in it depends on Manor.

### Before you update

Nothing: it updates itself as usual.

## 1.7.34

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.7.33

**A short tour of the review page the first time you open it.**

### What's new

- The first time you open Heiward's page after installing it, a tour walks you through it in three steps: what Heiward does, the choices that are yours (Developer mode, what it scans, automatic cleanup), and the page itself, part by part, each outlined as it goes. Skip it any time with Skip or Esc; take it again at `#/tour` on the page.
- It's Heiward's own, so it works the same with or without Manor. When Manor hires Heiward, its **Take the tour** opens it too, with a way back to Manor at the end.

### Before you update

Nothing: it updates itself as usual.

## 1.7.32

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.7.31

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.7.30

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.7.29

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.7.28

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.7.27

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.7.26

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.7.25

### What changed

- A release's notes now come from its entry in this changelog, after a first line naming the version, the commit it was built from and the kit it carries.

### Before you update

Nothing: it updates itself as usual.

## 1.7.24

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.7.23

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.7.22

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.7.21

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.7.20

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.7.19

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.7.18

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.7.17

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.

## 1.7.16

### What changed

- Small fixes and improvements.

### Before you update

- Nothing: it updates itself as usual.
