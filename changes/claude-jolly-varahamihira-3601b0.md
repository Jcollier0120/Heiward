**Heiward checks more carefully which folder each running program works in, before it offers a worktree for cleanup.**

### What changed

- To tell whether a worktree is in use, Heiward reads which folder each running program works in. A program that had only just started could be read before Windows had finished writing that folder down, and Heiward then took the half-written text as the folder's name. Now Heiward only accepts a full folder path, such as C:\… or \\server\share\…, with no stray characters in it. Anything else counts as not read yet, and Heiward reads it again the next time it checks.

### Before you update

- Nothing: it updates itself as usual.
