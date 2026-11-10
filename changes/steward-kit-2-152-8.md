**It carries kit 2.152.8: the parts all the staff share.**

### What changed

- Kit 2.152.6: Local AI's setup downloads three files at once, and a big file in four byte ranges side by side: its 2.8 GB on an RTX card comes in a few minutes, not over twenty.
- Kit 2.152.8: A big download comes in 8 MiB blocks on four connections, written in order, so each byte goes to disk once: kit 2.152.6's four ranges side by side had Windows write a big file 1.68 times over.

### Before you update

Nothing: it updates itself as usual.
