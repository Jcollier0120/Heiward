**Heiward writes far less to your disk as it scans: a save writes only the photos that changed.**

### What changed

- Heiward saves its list of scanned files and its AI cache as it scans: every five minutes during a long scan, and at the end of each scan that found something. Each save used to write the whole list and cache again, even for one new photo: about 92 MB for a library of 50,000 photos and 183 MB for 100,000. So a day of hourly scans that each found one new photo wrote about 2.2 GB, or 4.4 GB for 100,000 photos. A save now adds only what changed, under 2 KB for each new photo, and that day writes about 50 KB. Once the changes add up to a fifth of the list, Heiward writes the list whole once and starts adding again: on average about 7 KB more for each photo that changed. A scan that finds nothing new still writes nothing to them.
- If Heiward is stopped in the middle of a save (the PC loses power, or the process is ended), the list and the cache stay as the last finished save left them, and the next scan carries on from there.

### Before you update

- Nothing: it updates itself as usual. If you go back to an older version, it reads the list as it was last written whole, so its next scan reads again the photos that changed since then (at most a fifth of them). Nothing is lost.
