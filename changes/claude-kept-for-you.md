**Worktrees Reeve keeps because they hold your work are yours again, with Reeve's reason beside each.**

### What's new

- At a manor with Reeve, a worktree Reeve's worktree-tidy job keeps for a reason only you can sort out is no longer locked as "Reeve looks after it". It's tagged with Reeve's reason, such as "Reeve keeps it: has changes that aren't committed" or "Reeve keeps it: a git merge is under way in it", and says how long it has gone untouched. The reasons are changes that aren't committed, commits only on this PC, nothing on origin to check the work against, git-ignored files that aren't build output, and a merge or rebase under way.
- You can tick such a worktree to remove it. It's never ticked for you, Select all leaves it out, and automatic cleanup never takes it. Removing it asks first and says what goes with it. Its build output is deleted, and the rest goes to the Recycle Bin with git's record of the worktree, so you can put them back. Its branch and its commits stay in the repository. If the Recycle Bin can't take it, nothing is removed but its build output.

### What changed

- Every other worktree Reeve's tidy job goes through is still tagged "Reeve looks after it", and Reeve removes it once it's merged and clean.

### Before you update

- Update Reeve too. Until it says which worktrees it keeps for you, those stay tagged "Reeve looks after it", as before.
