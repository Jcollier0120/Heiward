**A scan of another of your PCs sorts its sets as fast as it checks its files, a scan with nothing new fetches nothing, and one video can't hold a scan up.**

### What changed

- Once a scan of another of your PCs has checked its files, it sorts them into sets: photos that look alike are looked at up close, lined up, and their words read, so they're fetched from the other PC again. It used to fetch them one at a time and wait for each, so sorting took longer than checking: on one library, 2¼ hours for 26.6 GB. Now it first works out which photos the sets will look at, without fetching any, then fetches them just ahead of the sorting, several at once, and each only once. In a test over a link like two PCs' on Wi-Fi, the same 200 photos were sorted in 14 seconds instead of 39.
- A scan with nothing new since the last one fetches nothing to sort its sets, and sorts them once. Before, it fetched about 100 MB of photos every time for answers it never got, such as a picture that can't be decoded or is too plain to line up. Heiward now remembers those until the photo changes.
- One video can no longer hold up the end of a scan. Some videos (from screen recorders and timelapses) have so few keyframes that each sampled point has to be decoded from far back, and Heiward then tried again without the graphics card. One such video held a scan up for 21 minutes. Now reaching a point takes at most a minute. Past that, the video is left out of the comparison, as a video that can't be decoded is, and the log names it. It isn't tried again while it's unchanged. This goes for scans of this PC too.
- The card for a scan of this PC by another of your PCs now gives the time the scan ended, not when it began. Findings kept by an older version are dated by when they came.
- A scan of another PC no longer tries to compare the sound of that PC's videos. It couldn't without fetching them whole, so it failed on every video in a set, every time.
- While another PC's photos are sorted into sets, lining them up keeps to the scan's share of this PC's processor.

### Before you update

- Nothing: it updates itself as usual. For the card's time, update Heiward on both PCs: a PC still on the older version shows when the scan began, as before.
