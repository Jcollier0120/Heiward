**Delete Highlighted and the cleanup dry run report are quick on big results.**

### What changed

- **Delete Highlighted** (Ctrl+Delete) takes the highlighted files off the list in one go. It used to take them off one at a time, searching the whole list for each one and updating the window's counts after each: 10,000 highlighted out of 50,000 froze the window for almost a second, and 20,000 out of 100,000 for about three seconds. It now takes about a third of a second for either, nearly all of it spent redrawing the list once. The files that stay keep their order, and **Undo Selection Change** (Ctrl+Z) works as before. As before, the files are only taken off the list, not deleted from disk.
- **Export Checked Items Cleanup Dry Run Report** sorts the list into its sets once, instead of searching the whole list for each set. A report covering 8,000 sets out of 50,000 files took over three seconds and one covering 16,000 sets out of 100,000 over twenty. Both now take under a tenth of a second. The report is the same file as before, with the same files in the same order.

### Before you update

- Nothing: it updates itself as usual.
