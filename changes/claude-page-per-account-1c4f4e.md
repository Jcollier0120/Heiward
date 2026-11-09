**Heiward's page answers only your own Windows account, and the Developer area no longer runs git where a repository's own settings could run a program.**

### What changed

- On a PC where more than one person signs in, each Windows account's Heiward page now answers only that account. Before, another account on the same PC could open your Heiward page and see your photos' thumbnails and paths, or clean them up. Now every other account, and any program of theirs, is refused.
- If another account's Heiward already uses the usual port (18484), your Heiward takes a free port of its own and remembers it, so both accounts have their own page. The Start menu, the Heiward shortcut and notifications open yours.
- Developer mode: Heiward no longer runs git in a repository whose own git settings could make git run a program, or reach another computer, such as a folder unzipped from the internet with a `.git` inside. That repository shows **Not checked with git** and why, and nothing in it is offered for cleanup. Every git Heiward runs now has git's file-watcher program and hooks turned off.
- Developer mode: a worktree record that points to a folder on another computer (a network share) is ignored.
- Developer mode: your Windows sign-in goes only to the Azure DevOps Server (TFS) hosts you list in `windowsSignInHosts` in `settings.json`, never to a server a repository names by itself. Other servers use the sign-in git keeps for them.
- The privacy policy now says that on AMD NPUs, Windows ML downloads the chip's NPU package from Microsoft itself.

### Before you update

- If more than one Windows account on your PC uses Heiward, the second account's page may move to a new port. If that account uses Castellan, Castellan's Heiward card in that account may show Heiward as not answering, because it looks on the usual port. Open Heiward from the Start menu instead.
- Developer mode with an Azure DevOps Server (TFS) at work: to keep seeing its pull requests with your Windows sign-in, add the server's name to `windowsSignInHosts` in `%LOCALAPPDATA%\Heiward\settings.json`, for example `"windowsSignInHosts": ["tfs"]`. Or sign in to it once with git (Git Credential Manager).
- Developer mode: repositories that set `core.hooksPath` (for example with husky), `core.fsmonitor`, a filter or a credential helper in their own git settings now show **Not checked with git**, and their worktrees and merged branches aren't offered for cleanup.
