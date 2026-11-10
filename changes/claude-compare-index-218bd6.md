**Big photo libraries now compare in seconds instead of minutes, with exactly the same results.**

### What changed

- Finding duplicates used to check every file against every other file, so the work grew with the square of the library: 50,000 photos meant 1.25 billion comparisons. Heiward now first finds, for each file, the few files that could possibly match it, and compares only those, in the same order and by the same rules as before. The groups, differences and flags it finds are exactly the same.
- Measured on a test library on the same PC: 50,000 photos now compare in 1.2 seconds (before: 17.6 seconds), 100,000 photos in 2.3 seconds (before: 91 seconds). With AI matching on: 2.5 seconds for 50,000 (before: 21) and 6.8 seconds for 100,000 (before: 108). Video libraries, mirrored-image matching and pHash matching get faster too.
- With "ignore black pixels" or "ignore white pixels" on, dark or bright pictures are compared on only a few of their pixels, so they can match much more, and Heiward still checks more of them; that setting gets a smaller speed-up (about 2.7× on a test library of 10,000 photos).
- The log now says how many comparisons the compare phase made, out of how many, and how long it took.

### Before you update

- Nothing: it updates itself as usual.
