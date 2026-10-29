**The Developer area shows the worktrees on every drive Heiward checks, and tags only those Reeve really looks after.**

### What's new

- Worktrees on a drive Heiward checks are listed even when their repository is on a drive scanned only when you ask (a worktree in E:\worktrees of a clone on D:\, say). They're measured without scanning that drive (only git's one-line record of each worktree is read there) and never ticked for you. When you clean one, Heiward checks it with git first, as it does for any worktree.

### What changed

- At a manor with Reeve, only the worktrees Reeve's worktree-tidy job goes through are tagged "Reeve looks after it". Any other worktree is yours: shown untagged, never ticked for you, and cleaned when you tick it, with Heiward's usual checks (nothing uncommitted, nothing unpushed, nothing working in it).
- Each worktree is shown by the drive it's really on (E:\worktrees\x, not C:\worktrees\x through a link), as Reeve shows it.
- A worktree that has been deleted from a drive Heiward checks no longer stays listed from an earlier check.

### Before you update

- Update Reeve too. Until it says which worktrees its tidy job goes through, Heiward tags them all "Reeve looks after it", as before.
- The next developer check lists the worktrees it now finds, so it can take longer than usual the first time.
