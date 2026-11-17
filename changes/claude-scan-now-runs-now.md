**Scan now on a folder or a drive scans now, and says what it found.**

### What changed

- **Scan now on a folder, or Scan this drive now, scans now.** It used to answer the first scan's wait on a new install. The first scan of every drive then waited for the next scheduled scan, hours later, so the scan you asked for looked put off to that time. Now a folder's or one drive's scan leaves the first scan waiting. **Scan now** (every drive), **Scan now instead of at …** and **Scan by itself** still start the first scan, as before. Once **Scan this drive now** has scanned every drive, the first scan stops waiting too.
- **The page says how the scan went.** Once a scan you started from a folder or a drive has ended, a line tells you what it found, for example "Steam scanned: no photos or videos in it". If the scan read nothing, the line says why, for example "that folder isn't there now, or scans leave it out". It also says when the scan was stopped, or when it never started.
- **A folder that holds only what scans leave out says so.** A Steam folder keeps its games in steamapps, and scans leave that out, so Scan now on it finds nothing. The line now names that folder and the reason (game library), and says you can right-click it to include it in scans.
- **Until the first scan has run, the page names no scheduled scan.** Scheduled scans skip themselves until then. Before, a drive's page said "Next scheduled scan: today 08:13 PM" and offered **Scan now instead of at 08:13 PM**. It now says the first scan waits for you, and offers **Scan every drive now** beside **Scan this drive now**. The title bar and Settings say the same.
- **"Last scan took …" only after a scan that read the drive whole.** A folder's scan used to time the whole drive, so a drive never scanned said "Last scan took under a second" beside "Not scanned yet". Scan this drive now no longer changes the other drives' times either.
- **While another scan runs, the page says why Scan now waits.** It no longer only greys the button out. A folder's Scan now, and a drive's page, say that another scan is running and that this one can start once it has finished. If you ask while one is running, the message says the same.

### Before you update

- Nothing: it updates itself as usual.
