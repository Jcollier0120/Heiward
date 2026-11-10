**Heiward is gentler on your disk: a check that finds nothing new no longer rewrites what it already saved.**

### What changed

- When a check finds the same duplicates as last time, Heiward no longer saves the whole list of them again, or the fingerprints of your files that it keeps. It notes only when the check ran. On a library of 50,000 photos, on a drive Heiward has to list again every 6 hours, that's about 17.5 MB written per check down to a few hundred bytes, up to 70 MB a day.
- Heiward's settings and status files are saved only when something in them changes.
- Which photos are found as duplicates and look-alikes is unchanged.

### Before you update

- Nothing: it updates itself as usual.
