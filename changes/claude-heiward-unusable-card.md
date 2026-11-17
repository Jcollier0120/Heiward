**A graphics card Heiward can't use is no longer treated as broken.**

### What changed

- Some PCs list a graphics card that nothing on them can use, such as a Windows Sandbox, which lists the host PC's card. Heiward checks a card before it uses it for AI matching. When that check couldn't open the card, Heiward wrote a failure to its log, and it ran the check again before every scan. Now it takes such a card as no card. It runs AI matching on the processor (or the NPU), with nothing in its log that looks like a fault.
- Settings says so in one plain line, for example: "The NVIDIA GeForce RTX 4080 SUPER can't be used for AI matching on this PC, so it runs on the processor." `hei status` says the same.
- Heiward checks that card again only when something changes: a driver update, a GPU pack update, or a restart.
- A card that has worked and then fails still shows as a failure, as before.

### Before you update

- Nothing: it updates itself as usual.
