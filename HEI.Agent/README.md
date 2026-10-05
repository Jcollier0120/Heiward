# Heiward

Heiward tends your drives. It looks for likely duplicate photos and videos and, in developer mode, stale developer files, and lists them on a local review page. Nothing is deleted until you say so: files you tick go to the Recycle Bin, where you can restore them. Once you trust what it suggests, you can let it [clean up by itself](#automatic-cleanup).

A *heiward* (Middle English, "hedge warden") was the village officer who kept the hedges trimmed and the fences sound. The command is `hei`.

Heiward is free software under the GNU AGPL v3 ([License](../README.md#license)). The AI matching finds resized, recompressed, cropped, mirrored and edited copies, and runs on:
- the **NPU**: fast, and it barely uses power, so scans can run every hour. Heiward detects which NPU the PC has and downloads that vendor's runtime:
  - **Qualcomm Hexagon** (Snapdragon X, Windows on Arm): Qualcomm's QNN plugin;
  - **Intel AI Boost** (Core Ultra, Intel/AMD build): Intel's OpenVINO plugin, OpenVINO included;
  - **AMD Ryzen AI** (Intel/AMD build, Windows 11 24H2 or later): AMD's Vitis AI plugin, which Windows ML downloads and keeps updated.

  Intel and AMD support is new and hasn't been tried on those NPUs yet. On any other PC, or if the NPU can't run the model, AI matching falls back to the GPU or CPU;
- your **GPU** (any DirectX 12 GPU, through DirectML), if you choose it: scans every 6 hours on AC power, or only when you ask;
- the **CPU**: the same choice as the GPU.

Scheduled scans run in the background:
- Windows' efficiency mode (EcoQoS), on efficient cores at low clocks;
- below-normal priority, on half the cores;
- a hard cap on the processor: a quarter of it, and at most two cores' worth (`backgroundCpuPercent`). Windows holds the scan back once it has used its share, however idle the PC is. Efficiency mode and a low priority alone left an idle PC's processor to the scan;
- very low disk priority, as the search indexer has, so anything else using the drive goes first;
- on a Snapdragon, the NPU at its power-saving clocks: 3.2 ms per picture instead of 2.1, still several times faster than the pictures are read;
- as much as it can on the graphics chip's video decoder, which costs the capped processor a fraction: videos (H.264, HEVC, VP9, AV1) and iPhone photos decode there, several at once.

A scan you start with **Scan now**, and a scheduled one while the review page is open, runs at full speed instead: every core but one, at normal priority, normal disk priority, and the NPU at full clocks. Open the page during a background scan and it speeds up; close it and a scheduled scan steps back. To keep every scan in the background, turn off **Full speed when you're here** in the page's Settings (`"scanSpeed": "background"`).

**The graphics chip's video decoder** reads videos beside the processor at any pace. On a Snapdragon X2 a video costs the processor 15-25 ms there instead of 230-500 ms, and the frames are byte for byte the ones the processor decodes. Under a background scan's cap, 290 phone videos took 22 s instead of 56. At full speed each PC learns as the scan goes how many videos its GPU keeps up with, so a desktop's graphics card takes more than a laptop's.
- **Memory.** Each video on the GPU holds up to half a gigabyte for a 4K one. With **Use more memory to scan faster** on (`moreMemory`, the default), a background scan decodes one video per 4 GB of memory at once, up to 8; off, two.
- **Games.** While a game or another 3D program keeps the graphics chip busy, or anything runs full screen, a scan makes way: it runs in the background with less memory until that's closed, whatever the settings say. `heiward.log` says when.
- **Driver crashes.** If a scan ever stops while the GPU decodes, the next one decodes on the processor from then on and says so in the log; delete `gpu-decoding-off.txt` in the database folder to try the GPU again.
- **More than one graphics card.** A desktop with a graphics card and the processor's graphics, or two cards, uses one for all of this, and for AI matching when that runs on the GPU. The installer asks which one, suggesting the one with the most memory of its own. Settings on the page changes it at any time, except while a scan runs: a scan keeps the card it started on, so the choice waits until it finishes or you stop it. settings.json keeps the card by name (`gpu`). If that card is gone, scans use Windows' default card and the page says so.

What a rescan reads from the disk:
- **Only what changed, as the drive's change journal says.** NTFS records every file created, changed, renamed or deleted, and Heiward reads that record as a normal user. That tells it which folders changed since the last scan.
  - **Changes Heiward ignores:** those in places scans don't look (Windows, programs, app data, code repositories, your exclusions).
  - **Folders where photos and videos may have changed** are listed again, one by one, and compared with the last listing. A document saved in Documents changes nothing.
  - **A drive with nothing new** isn't read at all. When no drive has anything new, a scheduled scan doesn't run: nothing is listed, compared or written, and a sleeping hard disk stays asleep. The log says `scan skipped, nothing new`.
  - **The drive is walked** as before, folder by folder, when the journal can't vouch for the last listing:
    - the first scan;
    - drives without a journal (FAT, exFAT, network drives);
    - a journal made again, or overwritten past the last scan (a PC off for a long time, or a very busy drive);
    - changed settings, or a Heiward that lists differently (a new build alone keeps the listing: only one that changes how drives are listed walks them once);
    - folders added, moved or deleted where scans look;
    - once a week regardless.
  - **A walk** goes one disk at a time per physical disk, so a hard disk never serves two walks at once.
- **No check per file.** The listing says which files exist; the scan doesn't ask the disk again about each one.
- **Only new and changed files' contents.** A file whose size and dates match the scan database isn't opened. One whose dates changed but size didn't gets a 64 KB check, and a moved file is recognised without being read again.
- **Hashes once.** The byte-for-byte check behind "Identical copy" reads a whole file (up to 256 MB). Its hash is kept, and reused while the file keeps its size and date.

`hei scan` shows how each drive was listed in `heiward.log` (`unchanged`, `N folder(s) listed again`, or `walked:` and why). The listing and where the journal was read up to are in `%LOCALAPPDATA%\Heiward\listing`.

## Install

Download `Heiward-<version>-x64.exe` (Intel or AMD) or `Heiward-<version>-arm64.exe` (Arm, such as Snapdragon) from [Releases](https://github.com/Jcollier0120/Heiward/releases) and run it. There's nothing to install first: no .NET, no FFmpeg, no admin rights. Heiward downloads what it needs itself, checks every download against a pinned SHA-256, and runs its first scan, with the review page open in your browser to show its progress. On a PC without an NPU it asks whether the AI should run on the **GPU** or the **CPU**, and whether to scan every 6 hours or only when you ask.

<details>
<summary>Everything the installer sets up</summary>

1. copies itself to `%LOCALAPPDATA%\Programs\Heiward`;
2. downloads FFmpeg, ONNX Runtime and the DINOv2 model, plus the NPU pack for the PC's NPU. Every download is SHA-256 pinned, and AMD's plugin comes from Windows ML;
3. checks for an NPU. Without one, it asks whether the AI should run on the **GPU** or the **CPU**. On every PC it asks how hard scans should work: **in the background** (efficiency mode, slower) or **at full speed**;
4. schedules scans with Task Scheduler (per user):
   - With an NPU they run every hour. On battery they step aside in Battery Saver or below 30%.
   - On a GPU or CPU they run every 6 hours on AC power, or never on a schedule if you pick "only when I press Scan now";
5. starts the review page at sign-in; it stays up, scans paused or not. It opens the page in your browser once a day at sign-in, and only when duplicates wait for review (look-alikes wait on the page without calling you to it);
6. adds **Heiward** shortcuts to the Start menu and the desktop (they open the review page in your default browser: a new tab if it's open, a new window if not, starting Heiward if needed), registers the name and icon its notifications show and `heiward:` links (the review page's "Start Heiward"), and adds an entry in Apps & Features so Windows can uninstall it.

</details>

**Already downloaded?** If another copy of Heiward already has FFmpeg and the AI components, the installer copies them instead of downloading: `hei install --reuse-from <that copy's folder>`. It also looks in the folder it was started from, and `hei setup` in the installed copy's. A copy is kept only if it passes the same check as a download, and the NPU pack only if the model then really runs on the NPU.

**Updating:** run the new version's exe; it installs over the old one and scans again. Installing (and uninstalling) stops only the installed copy's scan and page: a `hei.exe` run from anywhere else, such as a USB drive or an unzipped release, carries on. If that copy's review page holds the port, it's asked to close so the installed copy's page opens instead; that copy's scans keep running. Each report records the build that made it (its version and commit), because the sets are that build's rules. A report from another build is set aside, even under the same version number: the page shows none of its sets and nothing is cleaned up from them. The next scan finds them again with the new build: the next one due, or Scan now. The same goes for the developer report, and for the Microsoft Store version, which updates by itself. Sets the old report already listed aren't announced as new.

**Opening or reloading the page doesn't scan.** When the page starts (opened, reopened after it exited, or restarted), it starts a scan only if one is due by the schedule: the last scan, finished or stopped, plus the interval between scans (`scanEveryMinutes`, at least 15). Otherwise it shows the last scan's results, and the title bar says when the next scan is due. A scan you stop isn't started again by the next reload. With scans only when you press Scan now (`scanEveryMinutes: 0`), the page never starts one. Paused scans and the Store version's setup start none either.

Unattended: `hei install --yes --device gpu` (add `--on-demand` for no scheduled scans, `--scan-speed background` or `full` for how hard scans work, `--gpu` with a graphics card's number or name on a PC with more than one (without it, the one with the most memory of its own), and `--no-browser` to leave the browser alone: the first scan then starts without a window, and the page opens only when you open it). It asks nothing, and exits with 0 once installed, or with another code and the reason otherwise. Preview every step without changing anything: `hei install --dry-run`.

Heiward does not change your browser's startup pages. Browsers protect those, and changing them is what browser hijackers do. The sign-in step opens a normal tab instead.

## What it decides, and what it leaves to you

Code decides everything shown on the page. No model output is trusted to delete anything.

| On the page | Meaning | Ticked for the Recycle Bin? |
|---|---|---|
| Identical copy | Byte-for-byte the same file (SHA-256) | yes |
| Smaller copy / More compressed copy / Saved again | The same picture pixel for pixel (grayscale match ≥ 99.5%), at a lower resolution or more compressed | yes |
| Edited version | The AI sees the same picture with colours, a filter or a flip changed (≥ 97%), or the names say so (`IMG_1.jpg` and `IMG_1_Original.jpg`, `IMG_1-edited.jpg`) | no |
| Edited, cropped, flipped, or a similar shot | Crops, flips, and different shots that look alike. Also animated pictures (GIF, WebP), which are compared by their first frame only, and a video without sound next to the same video with it (the one with sound is kept) | no |

**Not listed at all:**
- **Burst shots and retakes.** Photos numbered one after another, like `IMG_1234` and `IMG_1235`, or `20260101_120000_001` and `_002`, are different moments, even at 99% alike, which would otherwise pass for a resaved copy. So are photos named after the time they were taken a few seconds apart, like `20201105_205359` and `20201105_205401` (Samsung), `PXL_…` (Pixel) or `Screenshot_…`.
  - Heiward sorts the names in each folder and checks whether a photo sits in such a series. Two photos are shots of one burst when one of them does, and their numbers are at most 20 apart, or their times at most 5 minutes.
  - A set holds one shot of a burst at most: the kept photo, or else the shot most like it. The burst's other shots leave the set.
  - An image sequence's frames (`0084.png`, `0085.png`) are a series too, even where a still stretch makes two frames the same bytes: each is a frame the sequence needs.
  - A byte-identical copy of a burst shot, say in a backup folder, still shows up as a copy of that shot. `(1)`, ` - Copy`, `_Original` and `-edited` are the same shot, not the next one.
- **A video in another language, or with another soundtrack.** Older games ship each cutscene once per language, with the same pictures, which match frame for frame. Each is the game's own file, not a copy. A video leaves the set when any of these tells it apart from the kept one:
  - **The names** differ only by a language, in the file name or a folder above it: `intro_en.wmv` and `intro_de.wmv`, `intro.wmv` and `intro_fr.wmv`, `Movies\English\intro.bik` and `Movies\German\intro.bik`, `EN-US` and `EN-GB`. This holds even for the same bytes: a game without a German dub ships the English one twice, and opens both names.
  - **The audio tracks' language tags** differ, when both files have them (`ENG` and `GER`).
  - **The sound** differs: the two soundtracks' audio fingerprints match less than 90%, at the offset where they match best. Heiward makes a fingerprint only for the videos in the report, once, and keeps it with the scan's database. The same sound re-encoded, even to 24 kb/s WMA, scored 96% and more; another voice over the same music 79–85%. A copy whose sound is shifted by half a second (trimmed mid-second) scores like another soundtrack, so it isn't offered.
- **Pictures less than 75% alike** to the kept one (the percentage the page shows). The engine's sets chain, so a picture like one that is like another could end up in a set it has nothing to do with.

A folder's **Look-alikes** tab has **Skip all**: every look-alike set with a file in that folder is kept as it is and leaves the list, as one line in History, where **review again** brings them back.

**Which copy to keep:**
- **Photos:** the highest resolution, then the camera original (it has a capture date), then the oldest file, then the largest. File size alone isn't quality: a colour edit makes a bigger JPEG than the original.
- **Videos:** the longest, then the highest resolution, bitrate and frame rate.
- **Identical copies:** the one in a cloud-synced folder, then the one outside Downloads, Desktop or temp, then the one without "copy" or "(1)" in its name.

**Refused before anything is deleted:**
- files outside the group;
- a request that would leave no copy;
- files changed since the scan;
- files on network or removable drives, or larger than the drive's Recycle Bin allows, or on a drive set to "don't move files to the Recycle Bin". Windows would delete those permanently.

**Cloud-synced files** (iCloud Photos, OneDrive, Dropbox) are marked on the page, and you're asked before they go. Deleting one also deletes it from the cloud and your other devices. Files that exist only in the cloud are never read: reading one would download it.

## Automatic cleanup

Once you trust what the page ticks, you can let Heiward clean it up by itself. The **Automatic cleanup** card in the review page's Settings (the gear in the title bar) has a switch for duplicates and, in [developer mode](#developer-mode), one for developer leftovers, both off until you turn them on (or `hei auto --duplicates on --developer on`).

- **When:** after each scan, once something has been listed for 3 days (`afterDays`), counted from when you turned it on at the earliest. Each set and item on the page says when it goes, with a **Leave it** button. Developer items go right after the daily developer check, so "untouched for 30 days" is that day's answer.
- **Duplicates:** plain copies of photos (identical, or the same picture pixel for pixel) and byte-for-byte identical videos go to the Recycle Bin, through the same checks as the page's button. It leaves for you:
  - edits and look-alikes;
  - copies in a cloud-synced folder, since deleting one deletes it on every device;
  - a set whose kept file changed since the scan;
  - sets that look like a copy of a whole folder: 20 or more sets with copies in the same two folders (say `C:\Pictures` and `D:\Backup`) are most likely a backup. The page can allow those two folders.
- **Developer leftovers:** what the page ticks, of the kinds you pick: merged branches, temp files and crash dumps, build outputs of projects untouched for 30 days, clean and pushed worktrees untouched for 30 days, and emulator system images no emulator uses. Package caches and emulators always wait for you. They're deleted permanently, as with the button. [At a manor with Reeve](#at-a-manor-reeve-and-the-steward), worktrees and merged branches are Reeve's: only what a worktree removal left behind is still taken.
- **Afterwards:** a notification says what went. History on the page marks it **Automatic**, and the card shows the last run and anything it left alone. With automatic cleanup on, the page opens at sign-in only for new sets it leaves to you, not every day.

`hei auto` lists what's due and when; everything it does is also in `heiward.log`.

## What gets scanned

Every fixed drive: internal drives, and external disks that Windows reports as fixed. USB sticks, card readers and network drives are left out unless you add them to `folders`. The report only lists your own files, so some folders are left out, with everything inside:

| Left out | Why |
|---|---|
| `Windows`, `Program Files`, `ProgramData`, `Recovery`, `$Recycle.Bin` and other system folders at a drive's root | They belong to Windows and installed programs |
| `AppData` in every profile, other people's profiles | Browser caches, app icons and saves; other accounts' files |
| Game libraries: `steamapps`, `SteamLibrary`, `Epic Games`, `GOG Games`, `XboxGames`, `WindowsApps` and others | Textures and videos a game needs |
| Photo apps' own libraries: `*.photoslibrary`, `*.lrdata`, `*.lrlibrary`, `*.cocatalog` | Their originals and previews are managed by the app |
| Code repositories (a folder holding `.git`, `.hg` or `.svn`), `node_modules`, folders whose name starts with `.` | Test pictures and build copies belong to the project |
| `AccountPictures` | Windows' account picture at nine sizes |
| Folder links (junctions, symbolic links) | Scanned where they point, not twice |

A folder you add to `folders` is scanned even inside one of these, for example a folder of photos inside a dot-folder or under `AppData`; the rules still apply to the folders below it. Your own `excludeFolders` are different: they win over `folders`, so a listed folder inside one is skipped, and `hei scope` and the scan say so.

`hei scope` lists all of it; `hei scope --count` also counts the photos and videos per folder, without opening any file. Add your own with `excludeFolders`.

**On the page:** right-click a folder (or press the menu key on it) and choose **Include in scans** or **Leave out of scans**, whichever it isn't now. An exempt folder's page has the same button. It edits the same two lists:
- leaving a folder out adds it to `excludeFolders`, or takes it out of `folders` if that's where it came from;
- including one takes its own path out of `excludeFolders`, or adds it to `folders` if a built-in rule left it out.

If a wider rule of yours covers it (`Old*`, or a folder above it), the page asks before removing that rule, since the rule covers other folders too. A whole drive can't be left out this way. The next scan follows.

## Developer mode

Developer mode is off until you turn it on with the **Developer mode** switch in the review page's Settings (or `"developerMode": "on"`). Off, nothing of it is checked or shown: no Developer area on the home page, and no developer leftovers in automatic cleanup. Settings from before the switch said `"auto"`, which checked every PC: it now stays on only where automatic cleanup of developer leftovers is on.

**With Manor:** Manor's **Developer options** turn developer features on or off for every agent in the manor, Heiward's too. With Manor installed (its `settings.json` and `app` folder in `%USERPROFILE%\.manor`, or `MANOR_HOME`) and its `settings.json` saying `"developerOptions": true` or `false`, that decides developer mode everywhere: the Developer area, the daily check, automatic cleanup of developer leftovers, the page's developer requests, `hei dev` and `hei status`. Settings shows which way in place of the switch ("Manor's Developer options turn this on", in Manor's own name), with a link to change it in Manor. Heiward reads it fresh on every page load and poll, every scan and every command, so an open page follows a change at its next poll (15 seconds at most). Heiward's own `developerMode` stays as you left it: without Manor, or once Manor's settings don't say, the switch is back and decides again. Heiward keeps its place at the manor either way: only its developer features follow Manor's Developer options.

On, once a day, after a scheduled scan, it also looks for what development tools leave behind and recreate when needed. It lists them in the **Developer area** of the review page, beside your repositories' open pull requests. Nothing is cleaned until you press the button there, or turn on [automatic cleanup](#automatic-cleanup). Cleaning deletes permanently, not to the Recycle Bin, because tools rebuild or download it all again.

The page is organised by repository:
- **The overview** shows every repository at once. Each has a panel with its open pull requests (the first five) beside its cleanup: the biggest or ticked build outputs and worktrees (the first four), and its merged branches with their Prune button. Repositories with pull requests ready to merge or waiting for a review come first. Those with nothing open and nothing to clean share one line at the end.
- **Each repository** also has its own page with all of it: its open pull requests first, then a section per cleanup area (build outputs, worktrees and merged branches). The side pane lists the repositories, with how many pull requests wait for you beside them.
- **Any version control:** Git, Mercurial, Subversion, TFVC (a local workspace), Unity Version Control (Plastic SCM), Bazaar, Fossil, Jujutsu, Darcs, Pijul and Perforce (a `.p4config` file) are recognised, and each repository says which it uses. Worktrees and merged branches are git's; build outputs are found in all of them.
- **Projects:** "Group repositories into a project" bundles repositories that belong together, such as an app and its backend, under one name. The bundles are saved in `settings.json` (`devProjects`), and a project's page can take a repository out or ungroup it.
- **Shared by all projects:** package caches, emulators and temp files, since they belong to no single repository.

**Pull requests** are read from where each git repository's `origin` (or its only remote) is hosted:

| Host | Recognised by | Signed in with |
|---|---|---|
| GitHub, GitHub Enterprise | github.com, or a server named after it | the GitHub CLI (`gh auth login`), or git's sign-in |
| Azure DevOps | dev.azure.com, *.visualstudio.com, over https or ssh | git's sign-in, or the Azure CLI (`az login`) |
| Azure DevOps Server, TFS | a `/_git/` URL on any other server | Windows' sign-in for a server on this network, or git's sign-in |
| GitLab | gitlab.com, or a server named after it | git's sign-in; public projects need none |
| Bitbucket Cloud, Server and Data Center | bitbucket.org, a `/scm/` URL, or a server named after it | git's sign-in; public repositories need none |
| Gitea, Forgejo | codeberg.org, or a server named after them | git's sign-in; public repositories need none |

"Git's sign-in" is the one Git Credential Manager already keeps for the host. Heiward never asks for one: with none kept, the page says how to sign in. A sign-in goes only to the host it belongs to, and Windows' own only to a server whose address is on this network. Each pull request says what it waits for (ready to merge, a review, changes, failing checks, conflicts, a draft…) and opens on its host, where it's merged or reviewed. The page asks while it's open, keeps the answer two minutes, and **Refresh** asks again. TFVC, Subversion and the others have no pull requests to show.

Tick items anywhere; a selection bar at the bottom cleans them all at once.

| What | Recognised by | Ticked for you |
|---|---|---|
| Build outputs: `node_modules`, `bin`/`obj`, Gradle `build`/`.gradle`/`.cxx`, `target`, `.venv`, `.next` | the project file beside it (`package.json`, a `.csproj`, `build.gradle`, `Cargo.toml`, ...), so a folder that merely has the name is left alone | in projects untouched for 30 days (`staleProjectDays`), judged by git's own files and the project's top level |
| Build outputs with no project beside them: `bin`/`obj` | a `Debug` or `Release` folder inside (or `obj`'s `project.assets.json`), no `.csproj` beside it, and nothing in it that git tracks (untracked or ignored): what's left when a project is renamed or moved. Git repositories only | never, and never cleaned automatically: nothing proves a build made them, so each waits for you to look |
| Git worktrees | the repository's `.git\worktrees` | untouched for 30 days, no uncommitted changes, and every commit already on a remote. Git removes it (`git worktree remove`, which refuses a worktree with changes), and the branch stays. Never offered while it's in use: a running program works in it (its current folder is inside), a Claude Code session running now works in it (`%USERPROFILE%\.claude\sessions`), or a Claude Code session wrote its transcript in the last 24 hours (`%USERPROFILE%\.claude\projects`). Nor are worktrees in a tool's home (a dot-folder such as `~\.<tool>\...`, or app data) or used by a scheduled task. What a removal that stopped partway left is offered too, unless something works in it |
| Package caches: Gradle, NuGet, npm, Yarn, pnpm, pip, Maven, Cargo, Go | the tools' own cache folders | never. Blocked while the tool runs (Java for Gradle, dotnet or Visual Studio for NuGet) |
| Android emulators and system images | the AVD folder and the SDK's `system-images` | system images no emulator uses. Emulators themselves aren't ticked, since they hold app data. Blocked while an emulator runs |
| Temp files and crash dumps | `%TEMP%` entries untouched for 7 days (`tempOlderThanDays`), `%LOCALAPPDATA%\CrashDumps` | yes |

Deletion never follows a link (pnpm's `node_modules` are full of junctions into its store), leaves files in use alone, and re-checks each item just before deleting it. It uses Windows' extended paths (`\\?\`), so folders deeper than 260 characters and names ending in a dot or a space go too. What it has to leave, it says: the page and `heiward.log` name the first file or folder left in use and what holds it, a program working in the folder or one with a file open (as Windows' Restart Manager says), or else Windows' own reason. `hei dev` shows the last check; `hei dev --scan` checks now (both in developer mode).

**Merged branches.** Each repository with a remote gets a **Prune** button (except [at a manor with Reeve](#at-a-manor-reeve-and-the-steward), whose job it is):
- It fetches first (`git fetch --prune`), then deletes the local branches already merged into the remote's default branch (`origin/HEAD`, else `main` or `master`).
- It never deletes `main`, `master`, `develop`, `dev`, `trunk` or a branch checked out in any worktree.
- It uses `git branch -d`; `-D` only when git objects that the branch isn't merged into the current checkout, after re-checking that it is in the remote's default branch.
- Branches on the remote are never touched.

From a terminal: `hei dev --prune-branches <repo>`.

### At a manor: Reeve and the Steward

At a manor, the code housekeeping other employees own is theirs, and Heiward leaves it alone. Standalone Heiward (no Manor) keeps doing all of it, as above.

- **Worktrees and merged branches are Reeve's**, with Manor installed (its `settings.json` and `app` folder, as in [Developer mode](#developer-mode)) and Reeve too (`%USERPROFILE%\.reeve\app`, or `REEVE_HOME`'s `app`). Reeve's worktree-tidy job removes merged, clean worktrees and deletes merged local branches. Heiward still measures the worktrees and shows the space they take, read-only: never ticked ("Reeve looks after it"), never cleaned from the page or automatically, and no merged branches or Prune buttons (`hei dev --prune-branches` says so and exits with 1). The worktrees section says who looks after them, with a link to Reeve's page. What a worktree removal that stopped partway left behind (a folder git no longer knows) is disk, not git state, and stays Heiward's.
- **Pull requests are the Steward's**, with Manor installed and the Steward too (`%USERPROFILE%\.steward\app`, or `STEWARD_HOME`'s `app`). The Steward merges, catches up and releases the employees' pull requests. Heiward asks no host for them and shows no pull requests or "ready to merge" panels; the Developer area says the Steward merges them, with a link to its page.
- **Everything else is disk, and Heiward's:** build outputs, the bin and obj with no project beside them, package caches, temp files, crash dumps and emulator images.

A manor without Reeve, or without the Steward, leaves that part with Heiward, so nothing is left unowned. Heiward checks on every page load and poll, every scan and every command, as it does Manor's Developer options.

## Sharing the NPU

Other NPU tools on this PC can use the NPU at the same time. Heiward takes turns with the ones that use the same machine-wide lock, `%USERPROFILE%\.npu-agent\locks\npu`, whenever `%USERPROFILE%\.npu-agent` exists. (The folder name is historical, kept so every tool still finds the lock.) They wait their turn in the NPU queue they share (`npu.queue` next to the lock):
- **First come, first served.** Each tool waits in line, and the NPU passes straight to the next in line when the holder lets go.
- **A person first.** A request someone is waiting on goes ahead of a scan. A scan that has waited two minutes is served in its turn regardless.
- **Short turns.** Heiward holds the lock for at most 2 seconds at a time, then joins the back of the line, so a long scan shares the NPU instead of blocking it.
- A stuck holder is evicted after 10 minutes, and a waiter that crashed leaves the line within 15 seconds.

A tool that takes the lock without queueing, such as an older build, can still get in ahead of the line, but never at the same time as anyone else.

### The graphics cards too

The same tools now run models on graphics cards and the processor as well (the manor's accelerators, in the Steward's kit: `kit\spec\ACCELERATORS.md`), and Heiward takes part:
- **A card has its own lock and line.** When AI matching runs on a graphics card, Heiward takes that card's lock for each batch, as it does the NPU's: the folder `gpu-<name>` beside the NPU's lock (`gpu-nvidia-geforce-rtx-4090`, the card's name in lowercase with each run of other characters a dash; a second card of the same name ends `-2`), and its line `gpu-<name>.queue`, with the same rules. Heiward takes only a card's first lock; other tools may serve a card more than once at a time. The processor has no lock from Heiward: its turns are the manor's processor model server's, which Heiward doesn't use.
- **Auto without a working NPU uses a graphics card it has checked.** With `aiDevice` `auto`, AI matching runs on the NPU; with none (or one that's failing), on the graphics card in use once the GPU pack has run the model on it, with its current driver (`hei probe`, which the installer runs, and a scan or `hei setup` runs once for a card that hasn't been checked); otherwise on the processor.
- **A device that fails Heiward is left alone for 10 minutes.** When the NPU or a card can't run the model (its pack doesn't load, the model won't open, or it fails under a batch or a check), Heiward runs the scan's AI on the processor instead, notes the failure in its own `ai\accelerators\<id>.failed.json` beside its AI components (in the shared files' format), and says why in `heiward.log`, `hei status`, `hei status --json` (`lastFallback`) and the page's Settings. Auto leaves that device alone until 10 minutes have passed; the next success on it removes the note. These notes are Heiward's alone: the other tools' failure marks in `%USERPROFILE%\.npu-agent\accelerators` are about their model servers (GenieX, llama-server), not the runtimes Heiward runs in its own process, so Heiward neither reads nor writes them.
- **Games first.** While a game keeps the scan's own graphics card busy (a quarter of a 3D engine or more), scans step back; a game on another card doesn't slow them.

## Settings

`%LOCALAPPDATA%\Heiward\settings.json` (a [development build](#development-builds)'s is in `%LOCALAPPDATA%\Heiward-dev`). Every field has a default.

| Field | Default | |
|---|---|---|
| `scanAllDrives` | true | Every fixed drive, minus the folders above |
| `folders` | none | More folders to scan, e.g. a USB drive or `\\nas\photos` (the only ones when `scanAllDrives` is false). Scanned even inside a folder left out by default |
| `excludeFolders` | none | A path (`D:\Scans`), a folder name at any depth (`Backups`), or either with wildcards (`D:\Old\*`, `*.bak`). Wins over `folders` |
| `onRequestDrives` | none | Drives scanned only when you ask (`D:\`): right-click a drive on the page, **Scan only when I ask**. Scheduled scans, the home page's Scan now, automatic cleanup and the developer check leave them alone, not reading them at all, so an archive disk can sleep. Their photos and videos as their last scan found them still count: their sets stay listed, and a copy of one elsewhere is still found. Scan one from its own page (**Scan this drive now**, or `hei scan --drive D:\`) |
| `excludeExtensions` | none | e.g. `[".heic"]` |
| `aiDevice` | `auto` | `auto` (the NPU; without a working one, the graphics card once it has passed a check; else the CPU), `npu`, `gpu`, `cpu` |
| `gpu` | `""` | With more than one graphics card, the one for GPU work, by name as Windows lists it (a second card of the same model: `NVIDIA GeForce RTX 4070 #2`). It runs AI matching on the GPU, and decodes videos and iPhone photos. Empty: Windows' default, the card driving the main display. The installer and the page's Settings set it; a scan reads it when it starts |
| `scanEveryMinutes` | 60 with an NPU, 360 on a GPU or CPU | `0`: no scheduled scans, only "Scan now". Only new and changed files are processed |
| `scanOnBattery`, `minBatteryPercent` | true, 30 | |
| `scanSpeed` | `auto` | `auto`: Scan now, and scheduled scans while the review page is open, at full speed; other scans in the background. `background`: every scan in the background. `full`: every scan at full speed, scheduled ones too |
| `parallelism` | 0 | Files decoded at once; 0: every core but one at full speed, half of them in the background |
| `backgroundCpuPercent` | 0 | The most of the processor a background scan uses, in percent, the FFmpeg it starts included; 0: a quarter, and at most two cores' worth |
| `moreMemory` | true | The page's **Use more memory to scan faster**: in the background, the graphics chip decodes one video per 4 GB of memory at once (up to 8, about half a gigabyte each); `false`: two. Scans use less while a game or another 3D program runs either way |
| `keepHistory` | true | `false`: the page's History lists nothing new and keeps no file names; `heiward.log` leaves out developer paths and branch names too |
| `openPageAtSignIn` | true | Opens the page in the browser at sign-in: once a day, only when duplicates wait for review, not look-alikes (with automatic cleanup of duplicates on: only new sets it leaves to you). The page itself starts at sign-in either way |
| `port` | 18484 | The review page, at `http://heiward.localhost:18484/` (this PC only). A development build's is 28484 |
| `toast` | true | A notification when a scan finds new duplicates, or automatic cleanup cleans some up. New look-alikes get none: they're on the page the next time you open it |
| `developerMode` | `off` | `on`: also check once a day for developer leftovers ([Developer mode](#developer-mode)); the switch in Settings sets it. An older file's `auto` counts as on only with automatic cleanup of developer leftovers on. With Manor installed and its `developerOptions` true or false, Manor's Developer options decide instead and this waits, kept for when they don't |
| `staleProjectDays`, `tempOlderThanDays` | 30, 7 | When build outputs and temp files are ticked |
| `autoClean` | off | [Automatic cleanup](#automatic-cleanup): `duplicates` and `developer` (true/false), `developerKinds` (`branches`, `temp`, `buildOutputs`, `worktrees`, `systemImages`), `afterDays` (3; 0 to 90) |

## Commands

```
hei                 install (or, once installed, open the review page)
hei scan [--open]   scan now
hei stop            stop the scan that's running
hei pause           pause scheduled scans and stop the running one  [--minutes N] (without it: until resumed)
hei resume          resume scheduled scans
hei open            make sure the review page is up, and open it  [--no-browser]
hei status          settings, where AI matching runs, devices that failed lately, last scan, schedule, locks
hei status --json   the same essentials as one JSON object, for scripts and other tools (below)
hei scope [--count] what a scan looks at and leaves out
hei dev [--scan]    developer mode: build outputs, worktrees, caches, emulators, temp
hei dev --prune-branches <repo>   delete local branches merged into the remote's main/master (not at a manor with Reeve)
hei auto            automatic cleanup: what's due and when  [--duplicates on|off] [--developer on|off] [--after-days N]
hei setup           get FFmpeg and the AI components  [--reuse-from <folder>]
hei install         [--dry-run] [--yes] [--device npu|gpu|cpu] [--gpu <number|name|default>] [--on-demand] [--scan-speed background|full|auto] [--no-browser] [--remove-github-copy] [--reuse-from <folder>]
hei uninstall       [--purge] [--dry-run]
```

`hei status --json` prints one JSON object on one line and nothing else (exit code 0). Times are UTC (`2026-10-01T14:00:00Z`) or `null`:

| Field | |
|---|---|
| `app` | `"heiward"` |
| `running` | Scheduled scans are on duty: not paused with `hei pause` or the page's Pause. Whether a scan task exists is `scheduled` |
| `stoppedSince` | When the pause began; `null` when not paused |
| `pausedUntil` | When a timed pause ends; `null` when not paused, or paused until you resume |
| `scheduled` | Settings ask for scheduled scans and the scan task is registered and turned on |
| `nextScan` | The scan task's next run as Task Scheduler words it (local time, the PC's format), or `null` |
| `scanning` | A scan is running now |
| `lastScan` | When the last report was made; `null` before the first scan with this version |
| `toReview` | Sets in that report you haven't decided on yet |
| `page` | `url`: the review page's address; `up`: whether it answers now |
| `summary` | One short sentence, e.g. "Scans every hour, next at 15:00. 3 sets to review." |
| `device` | Where AI matching last ran (the last scan, or the install's or `hei setup`'s check): `"npu"`, `"gpu"` or `"cpu"`; `null` when it's off or hasn't run |
| `accelerator` | The same as the manor's tools name it: `"npu"`, `"cpu"`, or the card's `"gpu-<name>"` |
| `card` | The graphics card's name as Windows lists it, when it ran on one; else `null` |
| `lastFallback` | When that work was meant for another device, from where to where and why, in one line (`"NPU to GPU: the Qualcomm Hexagon NPU pack failed to load (…)"`), or that it stopped (`"GPU failed: …"`); `null` when it ran where it was meant to |
| `developerMode` | Whether [developer mode](#developer-mode) is on now |
| `developerModeBy` | Who decides it: `"manor"` (Manor's Developer options) or `"heiward"` (Heiward's own switch) |

Other tools (such as Manor) can drive Heiward with these: `hei status --json` to see where it stands, `hei pause` and `hei resume` to stop and restart its scheduled scans (its page stays up either way), and `hei open --no-browser` to make sure its review page is up.

While the review page is up, its `GET /api/ping` answers `{"app":"heiward","store":…,"exe":…}` and its scans at a glance, the same four fields the Steward's kit gives the other agents, for Manor's employee cards. Times are UTC or `null`; it reads only small files Heiward keeps, and asks Task Scheduler at most once a minute:

| Field | |
|---|---|
| `lastRunAt` | When the last scan ended, whoever started it (`last-scan.json`, which each scan writes as it ends). A scheduled scan that skipped itself (paused, on battery) or found another running didn't run. Before any scan wrote that file: when the last report was written; `null` before the first scan |
| `lastRunOk` | Whether that scan went through: a fresh report, or nothing new to scan. `false` when it failed, found none of its folders, or was stopped; `null` when unknown |
| `nextRunAt` | The scan task's next run, from Task Scheduler (a time already past moves on by the interval); `null` while paused, with scans only when asked, with no scan task (a development build has none), or when Task Scheduler's wording isn't a time this PC reads |
| `runningSince` | When the scan under way started; `null` when none is |

### Disk space for the manor: `GET /api/disk`

Heiward measures the PC's disks for the whole manor, so the other agents ask it rather than measure again (the Surveyor's PC check reads it, and it replaces Reeve's disk-caches job). Once an hour, starting a minute after the review page starts, its process takes a reading on a thread below normal priority: every fixed drive's size and free space, and the size of the big tool caches. The reading is saved to `disk.json` in Heiward's folder, and `GET /api/disk` answers from it: nothing is measured for a request. It's read-only and for this PC only, like the rest of the page: it answers only on 127.0.0.1 and to its own names (a foreign `Host` gets 421), sends no CORS headers (another web page can't read it), and needs no token, as `/api/ping` doesn't (a POST without the page's token gets 403, as everywhere on the page).

```json
{
  "app": "heiward",
  "measuredAt": "2026-10-04T13:00:00Z",
  "previousAt": "2026-10-04T12:00:00Z",
  "everyMinutes": 60,
  "status": "warn",
  "warnings": ["C:\\ is below 15% free: 120 GB free of 1000 GB (12%)", "gradle grew 6 GB since the reading before (1 GB to 7 GB)"],
  "thresholds": { "alertPercentFree": 10, "warnPercentFree": 15, "cacheGrowthBytes": 5368709120 },
  "drives": [
    { "root": "C:\\", "label": "Windows", "totalBytes": 1073741824000, "freeBytes": 128849018880, "percentFree": 12, "level": "warn" }
  ],
  "caches": [
    { "name": "gradle", "path": "C:\\Users\\me\\.gradle", "bytes": 7516192768, "files": 1234, "grewBytes": 6442450944, "sameAs": null },
    { "name": "npm-cache", "path": "C:\\Users\\me\\AppData\\Local\\npm-cache", "bytes": 1073741824, "files": 99, "grewBytes": null, "sameAs": null }
  ],
  "cachesBytes": 8589934592
}
```

| Field | |
|---|---|
| `measuredAt` | When the reading was taken (UTC); `null` before the first, and then `status` is `"unknown"` and the lists are empty |
| `previousAt` | The reading before, which `grewBytes` compares with; `null` for the first |
| `everyMinutes` | How often a reading is taken: 60 |
| `status` | `"alert"` when a drive is below `alertPercentFree`; `"warn"` when one is below `warnPercentFree`, or a cache grew more than `cacheGrowthBytes` since the reading before; else `"ok"`. The thresholds are those Reeve's disk-caches job had: 10%, 15% and 5 GB |
| `warnings` | One line for each of those, drives first |
| `drives` | Every fixed drive that's ready: `root` (`C:\`), `label`, `totalBytes`, `freeBytes` (free to this user), `percentFree` (one decimal) and `level` (`ok`, `warn` or `alert`, by the thresholds) |
| `caches` | The caches that exist, with `bytes` and `files` (links aren't followed, so nothing is counted twice), and `grewBytes` since the reading before (less than 0 when it shrank; `null` when that reading didn't have it). By the names Reeve's job used: `foundry` (`~\.foundry`), `geniex-cache` (`~\.cache\geniex`), `reeve` (`~\.reeve`), `gradle` (`~\.gradle`), `npm-cache` (`npm_config_cache`, else `%LOCALAPPDATA%\npm-cache`), `pnpm` (`%LOCALAPPDATA%\pnpm`), `android-sdk` (`%LOCALAPPDATA%\Android\Sdk`); and `nuget` (`~\.nuget\packages`) and `pip` (`%LOCALAPPDATA%\pip\Cache`). Where the Claude app is installed, `npm-cache`, `pnpm` and `android-sdk` are also measured in its package's `LocalCache\Local`, as `npm-cache (Claude package)` and so on: AppData writes made from a Claude session land there. |
| `sameAs` | For a Claude package copy that shows exactly the same files as the cache it's named after (a reading from a process whose AppData is redirected there): that cache's name. It isn't counted twice |
| `cachesBytes` | All the caches together, each counted once |

## The review page

It's laid out like File Explorer, so you can go where you care most instead of scrolling every duplicate on the PC:

- **This PC:** a card per drive with its free space, how many photos and videos it holds, how long its last scan took, and its sets of copies and space to free. Below the cards, the folders where cleaning up frees the most, and the history of what you've done:
  - A folder's cleanup, or its **Skip all**, is one line.
  - **Clear history** empties the list and forgets the file names in it. Sets you kept stay hidden, and "freed so far" stays.
  - **Keep a history** off (in Settings) lists nothing new.
  - **While a scan runs,** each drive it reads shows how far it has got: finding the files, then checking them, which every drive does at its own pace ("Checking files · 1,234 of 5,678"), then comparing them all together. A line above the cards says whether it runs in the background or at full speed. Drives it leaves alone (nothing changed, or scanned only when you ask) keep their details.
- **A folder:** the navigation tree on the left and the folder on the right:
  - Its subfolders in a details view you can sort by space to free, with only the duplicates that touch this folder below.
  - Copies and look-alikes are shown separately.
  - A copy kept in another folder is dimmed and says so.
  - One button moves every ticked copy in the folder to the Recycle Bin, keeping the kept file of each set.
- **Exempt folders** (system, programs, games, code, other accounts) are greyed out with the reason. Many of them together fold into one row. Folders without photos or videos are hidden behind a "show" link.

**Heiward's name** in the title bar goes back to This PC from anywhere.

**Back to the manor:** with Manor installed (its `settings.json` and `app` folder in `%USERPROFILE%\.manor`, or `MANOR_HOME`), the title bar starts with Manor's icon and "Back to" its name, a link to Manor's page, as on every agent's page in the manor; a narrow window shows the icon alone. Heiward serves the icon from its own address: the one Manor's page shows (kept for ten minutes), else the one in Manor's app folder, else a plain house, and never an SVG with anything in it that runs. Without Manor, the title bar is as it was.

**Where AI matching runs:** a badge in the title bar. It's green ("NPU ready", then "Running on the NPU" once a scan has used it). Otherwise it names the device and why: "No NPU available", "Unsupported NPU" (an NPU this version can't drive yet), "NPU not set up" (its pack isn't downloaded), or "NPU fell back" (it couldn't run the model; `heiward.log` says why). The install, `hei setup` and every scan write this to `ai-status.json`, so the page reads one small file and is right from the first visit.

**Scanning on its own:** the title bar has the controls, and a banner says when Heiward isn't scanning by itself.
- **Scan now** turns into **Stop scan** while a scan runs: the scan stops within a second, without a report (`hei stop`).
- **Pause** stops the scan that's running, and scheduled scans skip themselves: for an hour, 4 hours, until tomorrow morning, or until you resume (`hei pause`, `hei resume`). Scan now still works while paused.
- **Its scan task gone or turned off** in Task Scheduler: **Turn them back on** registers it again.
- **Heiward not running** (it stopped, or the PC slept): the page stays as it was, says so, and offers **Start Heiward**, a `heiward://start` link the installer registers. Once Heiward is back, the page reloads by itself.

**Settings:** the gear in the title bar opens every switch in one place: **Scans run** (in the background, at full speed when you're here, or always at full speed), **Automatic cleanup**, **Keep a history** (with Clear history), and **Developer mode** (with Manor installed, Manor's Developer options decide that one: [Developer mode](#developer-mode)). It also shows what the settings file sets that the page has no switch for (what's scanned, skipped file types, where AI matching runs), and where the file is.

**Themes:** the palette button in the title bar picks Match Windows (the default), Light, Dark, or one of six colour themes: Arcade, Onyx, Carbon, Tinsel, Rose Gold and Quest. The choice is kept in the browser. With Manor installed, Manor chooses the theme for every page in the manor, Heiward's too: the page follows the `theme` in Manor's `settings.json` from its next load, and the palette shows it, with a link to change it in Manor.

Folder names are read live from disk; counts come from the last scan (`index.json` next to the report).

## The review page is local only

- It listens on 127.0.0.1 and answers only its own names (`heiward.localhost`, `127.0.0.1`, `localhost`), so a DNS-rebinding page can't reach it. Browsers resolve every `*.localhost` name to this PC themselves, so `heiward.localhost` needs no hosts file and can't be pointed elsewhere.
- Every button needs a token that exists only inside the page it served, plus a same-origin Origin header.
- Thumbnails are served only for files in the current report.
- It stays up, as every agent's page does: the sign-in task starts it, and each scheduled scan and each install start it again if it has gone. Pausing stops scans, not the page.
- With Manor installed, it asks Manor's page on this PC (127.0.0.1) for Manor's icon, and serves it only when nothing in it runs.

## Build

```
powershell -ExecutionPolicy Bypass -File tools\kit.ps1
dotnet build HEI.Agent -c Release
dotnet test HEI.Core.Tests -c Release
dotnet test HEI.Agent.Tests -c Release
dotnet publish HEI.Agent -c Release -r win-arm64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
```

Use `-r win-x64` for Intel and AMD PCs. The single file (`hei.exe`) is about 50 MB. Set `HEIWARD_HOME` to keep a test copy's settings and report somewhere else.

The icon (`heiward.ico`, and `wwwroot\heiward.png` for notifications) is rendered from `wwwroot\favicon.svg` by `make-icon.ps1`; run it again after changing the mark.

### The kit

Heiward shares the manor's kit, which the [Steward](https://github.com/Jcollier0120/Steward) keeps and rolls out to every agent. `kit.json` pins its version and the parts Heiward takes: for now the `spec` part, the rules Heiward keeps with every program that takes turns on the NPU and the graphics cards (`NPU-QUEUE.md` and `ACCELERATORS.md`), and the cases each of them runs unchanged (`npu-queue-vectors.json`, which `NpuLockTests` and `AcceleratorsTests` run).

`tools\kit.ps1` fills the git-ignored `kit\` with them, at the pinned version (`kit\spec\…`), and does nothing when they're already there. It takes the first of: `-From <dir>` or `STEWARD_KIT`, a kit tree such as a Steward checkout's `kit\`, for development; `..\Steward\kit` at that version; `%USERPROFILE%\.steward\kits`, the cache every agent on the PC shares; then the kit release `kit-v<version>` on GitHub, over HTTPS with no sign-in, checked against its `SHA256SUMS.txt`. Until it has run, HEI.Core.Tests doesn't build, and says so. `release.ps1`, `store.ps1` and the pull-request build run it first, and a release takes only the pinned kit.

Never edit `kit\`: the kit changes in the Steward, as a new kit version. The Steward's `bump` then moves the pin in a PR of its own, with Heiward's version raised, after running `tools\kit.ps1` and `dotnet test HEI.Core.Tests`. New or changed cases fail here until the C# follows them.

### Development builds

A `hei.exe` built in a checkout is a development build: a folder above it (up to eight) holds `.git`, a folder or a git worktree's file. It runs beside the installed Heiward without touching it:
- its settings, report and caches are in `%LOCALAPPDATA%\Heiward-dev`, not `%LOCALAPPDATA%\Heiward`;
- its review page is on port 28484, not 18484. A `port` in its own settings.json stands, unless it's the installed copy's 18484;
- it has no scheduled tasks: it scans when you press Scan now, or run `hei scan`.

The NPU lock is the same machine-wide one, and so are the graphics cards' locks. To try one without the installed tools seeing it, point `NPU_AGENT_NPU_LOCK` at a scratch folder's `locks\npu`: the cards' locks go beside it. Its failure notes are its own, in its `ai\accelerators`. `HEIWARD_HOME` overrides the data folder as before, and then the port is 18484 unless settings.json says otherwise.

`hei install` and `hei uninstall` act on the installed copy, from a development build too: install copies the exe to `%LOCALAPPDATA%\Programs\Heiward`, and the settings, downloads, tasks and shortcuts it sets up are the installed copy's. To try development work as the real Heiward, build a release with `release.ps1` (below) and run it, as you would a download.

### Making a release

From the repository root, on the commit to release, with the .NET 10 SDK and the GitHub CLI (`gh`, signed in) on PATH:

```
powershell -ExecutionPolicy Bypass -File HEI.Agent\release.ps1 -Publish
```

It builds `Heiward-<version>-x64.exe` and `Heiward-<version>-arm64.exe` into `artifacts\heiward`, writes `SHA256SUMS.txt`, then creates the release `v<version>` at the commit it built and uploads all three. The version is `VersionPrefix` in `HEI.Agent.csproj`. It refuses to publish with uncommitted changes, or when that release already exists.

- It fills the kit first (`tools\kit.ps1`, above), at the version `kit.json` pins, and refuses a kit tree from `STEWARD_KIT`.
- Without `-Publish` it only builds.
- `-Notes <text or file>` replaces the default release notes, which say which file to download and how to get past the unsigned-build warning.

GitHub attaches the source code to every release by itself.
