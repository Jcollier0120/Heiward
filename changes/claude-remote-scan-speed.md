**A scan of another of your PCs no longer waits on the link between them: hours for a big library, not days.**

### What changed

- When another of your PCs scans this one, it now asks for the photos and videos just ahead of the one it's checking, several at once, instead of one piece at a time and waiting for each. A photo comes over in one go, and a video's header and index come ahead of it. Over Wi-Fi, where each ask waits for the link, this was most of the scan's time. On a link where each round trip takes about a tenth of a second, the same photos and videos are now checked in half the time. The PC being scanned is as gentle as before: about 20 MB a second at most, at very low disk priority, and never while a game is played.
- While it makes room for the other PC's files, the card counts them as it goes. Before, it showed the whole count and stood still for minutes. The next step starts its count from nothing, without showing the old count first.
- The PC being scanned lists only its photos and videos. Files it already knows aren't one, such as a text file named .ts, are left out. Its log gives the one count.
- Listing a very big library for another PC's scan uses about a fifth less memory on the PC being scanned, and the scan itself uses less on the PC scanning.
- On a PC with many cores, a scan for another PC keeps to 8 cores' worth of the processor, and its log says how much that is of the whole. Before, it could take a little more, and its log said 50%.
- The scan's log now says, every 1,000 files, where its time goes: each ask to the other PC, each file's time, and the other PC's own reading. It also says what sorting the sets fetched again.

### Before you update

- Update Heiward on both PCs: the PC being scanned reports its side of the timings only from this version on. A scan already under way finishes as it began; the next one is the faster one.
