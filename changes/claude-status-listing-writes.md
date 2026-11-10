**Heiward writes less to your disk while it scans: only the files that changed go into its list of each drive, and a scan's progress is saved every few seconds instead of every second.**

### What changed

- To see what changed since the last scan, Heiward keeps a list of the files on each drive. It used to save that whole list again after every scan. It now adds only the files that changed, and saves the list whole again only once those additions are as big as the list. On a library of 50,000 photos with 1% of them changed, that's 4.2 MB down to about 40 KB a scan.
- While a scan runs, its progress for Heiward's page is saved every 3 seconds while the page is open and every 30 seconds while it isn't, instead of every second. Each new step of a scan still shows on the page at once.
- Which photos are found as duplicates and look-alikes is unchanged.

### Before you update

- Nothing: it updates itself as usual.
- If you go back to an earlier version later, its first scan lists each drive again from the start, once. On a large drive that can take a minute or two, and nothing is missed.
