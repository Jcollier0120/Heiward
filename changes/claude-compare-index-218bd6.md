**Big photo libraries now compare in seconds instead of minutes, with exactly the same results.**

### What changed

- Finding duplicates used to check every file against every other file, so the work grew with the square of the library: 50,000 photos meant 1.25 billion comparisons. Heiward now first finds, for each file, the few files that could possibly match it, and compares only those, in the same order and by the same rules as before. The groups, differences and flags it finds are exactly the same.
- Measured on a test library on the same PC: 50,000 photos now compare in 1.2 seconds (before: 17.6 seconds), 100,000 photos in 2.3 seconds (before: 91 seconds). With AI matching on: 2.5 seconds for 50,000 (before: 21) and 6.8 seconds for 100,000 (before: 108). Video libraries, mirrored-image matching and pHash matching get faster too.
- With "ignore black pixels" or "ignore white pixels" on, each comparison is now about 9 times faster on PCs with ARM processors (such as Snapdragon), which had been using a slow way to compare: 10,000 photos with "ignore black pixels" now take 0.9 seconds (before: 6.8 seconds). Dark or bright pictures are compared on only a few of their pixels, so they can match far more files, and Heiward still has to check more of those pairs: 50,000 photos with "ignore black pixels" take about 11 seconds.
- The log now says how many comparisons the compare phase made, out of how many, and how long it took.

### Before you update

- Nothing: it updates itself as usual.
