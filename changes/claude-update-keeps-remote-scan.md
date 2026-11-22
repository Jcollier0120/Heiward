**An update no longer cuts a scan of another of your PCs, or leaves Heiward down when it fails.**

### What changed

- An update waits while one of your PCs is scanning another's photos and videos, on both PCs, for up to a day from the scan's start. Before, an update could stop a scan many hours in and lose it.
- If a scan for another PC is stopped anyway (an update past that day, the link lost, or Stop), the next scan picks up where it stopped: the files it checked aren't read again. Before, the AI's findings so far were lost whenever its process ended early, and the next scan read every photo again.
- A scan of this PC that stopped part way now says so on the card: why it stopped (for example, that Heiward on the other PC is being updated), how far it got, and **Scan again**. The card says this even after Heiward restarts. Before, it said only that the other PC stopped answering, with no way on.
- An update now waits for every Heiward process to exit before it replaces Heiward, a scan for another PC included, and tries again while the old file is still in use. If an update fails anyway, Heiward starts again as it was, and its log says so. Before, a failed update could leave Heiward stopped until it was reinstalled.

### Before you update

- Update Heiward on both PCs. A scan already under way on the old version finishes or stops as before: the next one picks up where a stopped one left off.
