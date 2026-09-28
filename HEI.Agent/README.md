# Heiward

Heiward tends your drives. It looks for likely duplicate photos and videos and, in developer mode, stale developer files, and lists them on a local review page. Nothing is deleted until you say so: files you tick go to the Recycle Bin, where you can restore them. Once you trust what it suggests, you can let it [clean up by itself](#automatic-cleanup).

A *heiward* (Middle English, "hedge warden") was the village officer who kept the hedges trimmed and the fences sound. The command is `hei`.

Heiward is based on [Video Duplicate Finder](https://github.com/0x90d/videoduplicatefinder) and uses its engine. Like it, Heiward is free software under the GNU AGPL v3. The AI matching finds resized, recompressed, cropped, mirrored and edited copies, and runs on:
- the **NPU**: fast, and it barely uses power, so scans can run every hour. Heiward detects which NPU the PC has and downloads that vendor's runtime:
  - **Qualcomm Hexagon** (Snapdragon X, Windows on Arm): Qualcomm's QNN plugin;
  - **Intel AI Boost** (Core Ultra, Intel/AMD build): Intel's OpenVINO plugin, OpenVINO included;
  - **AMD Ryzen AI** (Intel/AMD build, Windows 11 24H2 or later): AMD's Vitis AI plugin, which Windows ML downloads and keeps updated.

  Intel and AMD support is new and hasn't been tried on those NPUs yet. On any other PC, or if the NPU can't run the model, AI matching falls back to the GPU or CPU;
- your **GPU** (any DirectX 12 GPU, through DirectML), if you choose it: scans every 6 hours on AC power, or only when you ask;
- the **CPU**: the same choice as the GPU.

Scheduled scans run in Windows' efficiency mode (EcoQoS), on efficient cores at low clocks, at below-normal priority. A rescan only checks new and changed files.

## Install

Download `Heiward-<version>-x64.exe` (Intel or AMD) or `Heiward-<version>-arm64.exe` (Arm, such as Snapdragon) from [Releases](https://github.com/Jcollier0120/Heiward/releases) and run it. There's nothing to install first: no .NET, no FFmpeg, no admin rights. Heiward downloads what it needs itself, checks every download against a pinned SHA-256, and runs its first scan, with the review page open in your browser to show its progress. On a PC without an NPU it asks whether the AI should run on the **GPU** or the **CPU**, and whether to scan every 6 hours or only when you ask.

<details>
<summary>Everything the installer sets up</summary>

1. copies itself to `%LOCALAPPDATA%\Programs\Heiward`;
2. downloads FFmpeg, ONNX Runtime and the DINOv2 model, plus the NPU pack for the PC's NPU. Every download is SHA-256 pinned, and AMD's plugin comes from Windows ML;
3. checks for an NPU. Without one, it asks whether the AI should run on the **GPU** or the **CPU**;
4. schedules scans with Task Scheduler (per user):
   - With an NPU they run every hour. On battery they step aside in Battery Saver or below 30%.
   - On a GPU or CPU they run every 6 hours on AC power, or never on a schedule if you pick "only when I press Scan now";
5. opens the review page in your browser once a day at sign-in, and only when something waits for review;
6. adds **Heiward** to the Start menu, registers the name its notifications show, and adds an entry in Apps & Features so Windows can uninstall it.

</details>

**Already downloaded?** If another copy of Heiward or Video Duplicate Finder already has FFmpeg and the AI components, the installer copies them instead of downloading: `hei install --reuse-from <that copy's folder>`. It also looks in the folder it was started from, and in Video Duplicate Finder's own per-user folder. A copy is kept only if it passes the same check as a download, and the NPU pack only if the model then really runs on the NPU.

Unattended: `hei install --yes --device gpu` (add `--on-demand` for no scheduled scans). Preview every step without changing anything: `hei install --dry-run`.

Heiward does not change your browser's startup pages. Browsers protect those, and changing them is what browser hijackers do. The sign-in step opens a normal tab instead.

## What it decides, and what it leaves to you

Code decides everything shown on the page. No model output is trusted to delete anything.

| On the page | Meaning | Ticked for the Recycle Bin? |
|---|---|---|
| Identical copy | Byte-for-byte the same file (SHA-256) | yes |
| Smaller copy / More compressed copy / Saved again | The same picture pixel for pixel (grayscale match ≥ 99.5%), at a lower resolution or more compressed | yes |
| Edited version | The AI sees the same picture with colours, a filter or a flip changed (≥ 97%), or the names say so (`IMG_1.jpg` and `IMG_1_Original.jpg`, `IMG_1-edited.jpg`) | no |
| Edited, cropped, flipped, or a similar shot | Crops, flips, and different shots that look alike, such as bursts. Also animated pictures (GIF, WebP), which are compared by their first frame only | no |

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

Once you trust what the page ticks, you can let Heiward clean it up by itself. The **Automatic cleanup** card on the review page has a switch for duplicates and one for developer leftovers, both off until you turn them on (or `hei auto --duplicates on --developer on`).

- **When:** after each scan, once something has been listed for 3 days (`afterDays`), counted from when you turned it on at the earliest. Each set and item on the page says when it goes, with a **Leave it** button. Developer items go right after the daily developer check, so "untouched for 30 days" is that day's answer.
- **Duplicates:** plain copies of photos (identical, or the same picture pixel for pixel) and byte-for-byte identical videos go to the Recycle Bin, through the same checks as the page's button. It leaves for you:
  - edits and look-alikes;
  - copies in a cloud-synced folder, since deleting one deletes it on every device;
  - a set whose kept file changed since the scan;
  - sets that look like a copy of a whole folder: 20 or more sets with copies in the same two folders (say `C:\Pictures` and `D:\Backup`) are most likely a backup. The page can allow those two folders.
- **Developer leftovers:** what the page ticks, of the kinds you pick: merged branches, temp files and crash dumps, build outputs of projects untouched for 30 days, clean and pushed worktrees untouched for 30 days, and emulator system images no emulator uses. Package caches and emulators always wait for you. They're deleted permanently, as with the button.
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

## Developer mode

Once a day, after a scheduled scan, it also looks for what development tools leave behind and recreate when needed. It lists them under **Developer cleanup** on the review page. Nothing is cleaned until you press the button there, or turn on [automatic cleanup](#automatic-cleanup). Cleaning deletes permanently, not to the Recycle Bin, because tools rebuild or download it all again.

The page is organised by project:
- **Each repository** has its own page, with a section per cleanup area: build outputs, worktrees and merged branches.
- **Projects:** "Group repositories into a project" bundles repositories that belong together, such as an app and its backend, under one name. The bundles are saved in `settings.json` (`devProjects`), and a project's page can take a repository out or ungroup it.
- **Shared by all projects:** package caches, emulators and temp files, since they belong to no single repository.

Tick items anywhere; a selection bar at the bottom cleans them all at once.

| What | Recognised by | Ticked for you |
|---|---|---|
| Build outputs: `node_modules`, `bin`/`obj`, Gradle `build`/`.gradle`/`.cxx`, `target`, `.venv`, `.next` | the project file beside it (`package.json`, a `.csproj`, `build.gradle`, `Cargo.toml`, ...), so a folder that merely has the name is left alone | in projects untouched for 30 days (`staleProjectDays`), judged by git's own files and the project's top level |
| Git worktrees | the repository's `.git\worktrees` | untouched for 30 days, no uncommitted changes, and every commit already on a remote. Git removes it (`git worktree remove`, which refuses a worktree with changes), and the branch stays. Worktrees in a tool's home (`~\.npu-agent\...`, app data) or used by a scheduled task are never offered |
| Package caches: Gradle, NuGet, npm, Yarn, pnpm, pip, Maven, Cargo, Go | the tools' own cache folders | never. Blocked while the tool runs (Java for Gradle, dotnet or Visual Studio for NuGet) |
| Android emulators and system images | the AVD folder and the SDK's `system-images` | system images no emulator uses. Emulators themselves aren't ticked, since they hold app data. Blocked while an emulator runs |
| Temp files and crash dumps | `%TEMP%` entries untouched for 7 days (`tempOlderThanDays`), `%LOCALAPPDATA%\CrashDumps` | yes |

Deletion never follows a link (pnpm's `node_modules` are full of junctions into its store), leaves files in use alone, and re-checks each item just before deleting it. `hei dev` shows the last check; `hei dev --scan` checks now. Set `"developerMode": "off"` to turn it off.

**Merged branches.** Each repository with a remote gets a **Prune** button:
- It fetches first (`git fetch --prune`), then deletes the local branches already merged into the remote's default branch (`origin/HEAD`, else `main` or `master`).
- It never deletes `main`, `master`, `develop`, `dev`, `trunk` or a branch checked out in any worktree.
- It uses `git branch -d`; `-D` only when git objects that the branch isn't merged into the current checkout, after re-checking that it is in the remote's default branch.
- Branches on the remote are never touched.

From a terminal: `hei dev --prune-branches <repo>`.

## Sharing the NPU

Other NPU tools on the PC can use the NPU at the same time, for example npu-agent's maintenance jobs, which run a local LLM on the NPU. Heiward takes the same machine-wide lock they use (`%USERPROFILE%\.npu-agent\locks\npu`), when it exists:
- it holds the lock for at most 2 seconds at a time, so the other tool never waits longer than that;
- a stuck holder is evicted after 10 minutes.

## Settings

`%LOCALAPPDATA%\Heiward\settings.json`. Every field has a default.

| Field | Default | |
|---|---|---|
| `scanAllDrives` | true | Every fixed drive, minus the folders above |
| `folders` | none | More folders to scan, e.g. a USB drive or `\\nas\photos` (the only ones when `scanAllDrives` is false). Scanned even inside a folder left out by default |
| `excludeFolders` | none | A path (`D:\Scans`), a folder name at any depth (`Backups`), or either with wildcards (`D:\Old\*`, `*.bak`). Wins over `folders` |
| `excludeExtensions` | none | e.g. `[".heic"]` |
| `aiDevice` | `auto` | `auto` (NPU, else CPU), `npu`, `gpu`, `cpu` |
| `scanEveryMinutes` | 60 with an NPU, 360 on a GPU or CPU | `0`: no scheduled scans, only "Scan now". Only new and changed files are processed |
| `scanOnBattery`, `minBatteryPercent` | true, 30 | |
| `openPageAtSignIn` | true | Once a day, only when something waits for review (with automatic cleanup of duplicates on: only new sets it leaves to you) |
| `port` | 18484 | The review page, at `http://heiward.localhost:18484/` (this PC only) |
| `toast` | true | A notification when a scan finds something new |
| `developerMode` | `auto` | `off`: no developer cleanup. `auto`: check once a day |
| `staleProjectDays`, `tempOlderThanDays` | 30, 7 | When build outputs and temp files are ticked |
| `autoClean` | off | [Automatic cleanup](#automatic-cleanup): `duplicates` and `developer` (true/false), `developerKinds` (`branches`, `temp`, `buildOutputs`, `worktrees`, `systemImages`), `afterDays` (3; 0 to 90) |

## Commands

```
hei                 install (or, once installed, open the review page)
hei scan [--open]   scan now
hei open            open the review page
hei status          settings, where AI matching runs, last scan, schedule, NPU lock
hei scope [--count] what a scan looks at and leaves out
hei dev [--scan]    developer mode: build outputs, worktrees, caches, emulators, temp
hei dev --prune-branches <repo>   delete local branches merged into the remote's main/master
hei auto            automatic cleanup: what's due and when  [--duplicates on|off] [--developer on|off] [--after-days N]
hei setup           get FFmpeg and the AI components  [--reuse-from <folder>]
hei install         [--dry-run] [--yes] [--device npu|gpu|cpu] [--on-demand] [--reuse-from <folder>]
hei uninstall       [--purge] [--dry-run]
```

## The review page

It's laid out like File Explorer, so you can go where you care most instead of scrolling every duplicate on the PC:

- **This PC:** a card per drive with its free space, how many photos and videos it holds, how long its last scan took, and its sets of copies and space to free. Below the cards, the folders where cleaning up frees the most, and the history of what you've done.
- **A folder:** the navigation tree on the left and the folder on the right:
  - Its subfolders in a details view you can sort by space to free, with only the duplicates that touch this folder below.
  - Copies and look-alikes are shown separately.
  - A copy kept in another folder is dimmed and says so.
  - One button moves every ticked copy in the folder to the Recycle Bin, keeping the kept file of each set.
- **Exempt folders** (system, programs, games, code, other accounts) are greyed out with the reason. Many of them together fold into one row. Folders without photos or videos are hidden behind a "show" link.

**Where AI matching runs:** a badge in the title bar. It's green ("NPU ready", then "Running on the NPU" once a scan has used it). Otherwise it names the device and why: "No NPU available", "Unsupported NPU" (an NPU this version can't drive yet), "NPU not set up" (its pack isn't downloaded), or "NPU fell back" (it couldn't run the model; `heiward.log` says why). The install, `hei setup` and every scan write this to `ai-status.json`, so the page reads one small file and is right from the first visit.

**Themes:** the palette button in the title bar picks Match Windows (the default), Light, Dark, or one of six colour themes: Arcade, Onyx, Carbon, Tinsel, Rose Gold and Quest. The choice is kept in the browser.

Folder names are read live from disk; counts come from the last scan (`index.json` next to the report).

## The review page is local only

- It listens on 127.0.0.1 and answers only its own names (`heiward.localhost`, `127.0.0.1`, `localhost`), so a DNS-rebinding page can't reach it. Browsers resolve every `*.localhost` name to this PC themselves, so `heiward.localhost` needs no hosts file and can't be pointed elsewhere.
- Every button needs a token that exists only inside the page it served, plus a same-origin Origin header.
- Thumbnails are served only for files in the current report.
- It stops after an hour unused.

## Build

```
dotnet build HEI.Agent -c Release
dotnet test HEI.Agent.Tests -c Release
dotnet publish HEI.Agent -c Release -r win-arm64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
```

Use `-r win-x64` for Intel and AMD PCs. The single file (`hei.exe`) is about 50 MB. Set `HEIWARD_HOME` to keep a test copy's settings and report somewhere else.

### Making a release

From the repository root, on the commit to release, with the .NET 10 SDK and the GitHub CLI (`gh`, signed in) on PATH:

```
powershell -ExecutionPolicy Bypass -File HEI.Agent\release.ps1 -Publish
```

It builds `Heiward-<version>-x64.exe` and `Heiward-<version>-arm64.exe` into `artifacts\heiward`, writes `SHA256SUMS.txt`, then creates the release `v<version>` at the commit it built and uploads all three. The version is `VersionPrefix` in `HEI.Agent.csproj`. It refuses to publish with uncommitted changes, or when that release already exists.

- Without `-Publish` it only builds.
- `-Notes <text or file>` replaces the default release notes, which say which file to download and how to get past the unsigned-build warning.

GitHub attaches the source code to every release by itself.
