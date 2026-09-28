# VDF Agent: duplicate check in the background

`vdf-agent` looks through your photo and video folders for likely duplicates and lists them on a local review page. It never deletes anything on its own. Files you tick go to the Recycle Bin, where you can restore them.

It is built on Video Duplicate Finder's engine. The AI matching finds resized, recompressed, cropped, mirrored and edited copies, and runs on:
- the **NPU** of a Snapdragon PC: fast, and it barely uses power, so scans can run every hour;
- your **GPU** (any DirectX 12 GPU, through DirectML), if you choose it: scans every 6 hours on AC power, or only when you ask;
- the **CPU**: the same choice as the GPU.

Scheduled scans run in Windows' efficiency mode (EcoQoS), on efficient cores at low clocks, at below-normal priority. A rescan only checks new and changed files.

## Install

Download `vdf-agent.exe` and run it. It is one self-contained file: no .NET, no admin rights. It then:

1. copies itself to `%LOCALAPPDATA%\Programs\VDF Agent`;
2. downloads FFmpeg, ONNX Runtime and the DINOv2 model, plus the NPU pack on Snapdragon PCs. Every download is SHA-256 pinned;
3. checks for an NPU. Without one, it asks whether the AI should run on the **GPU** or the **CPU**;
4. schedules scans with Task Scheduler (per user):
   - With an NPU they run every hour. On battery they step aside in Battery Saver or below 30%.
   - On a GPU or CPU they run every 6 hours on AC power, or never on a schedule if you pick "only when I press Scan now";
5. opens the review page in your browser once a day at sign-in, and only when something waits for review;
6. adds **Duplicate check** to the Start menu, and an entry in Apps & Features so Windows can uninstall it.

Unattended: `vdf-agent install --yes --device gpu` (add `--on-demand` for no scheduled scans). Preview every step without changing anything: `vdf-agent install --dry-run`.

The agent does not change your browser's startup pages. Browsers protect those, and changing them is what browser hijackers do. The sign-in step opens a normal tab instead.

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

`vdf-agent scope` lists all of it; `vdf-agent scope --count` also counts the photos and videos per folder, without opening any file. Add your own with `excludeFolders`.

## Developer mode

Once a day, after a scheduled scan, it also looks for what development tools leave behind and recreate when needed. It lists them under **Developer cleanup** on the review page. Nothing is cleaned until you press the button there. Cleaning deletes permanently, not to the Recycle Bin, because tools rebuild or download it all again.

| What | Recognised by | Ticked for you |
|---|---|---|
| Build outputs: `node_modules`, `bin`/`obj`, Gradle `build`/`.gradle`/`.cxx`, `target`, `.venv`, `.next` | the project file beside it (`package.json`, a `.csproj`, `build.gradle`, `Cargo.toml`, ...), so a folder that merely has the name is left alone | in projects untouched for 30 days (`staleProjectDays`), judged by git's own files and the project's top level |
| Git worktrees | the repository's `.git\worktrees` | untouched for 30 days, no uncommitted changes, and every commit already on a remote. Git removes it (`git worktree remove`, which refuses a worktree with changes), and the branch stays. Worktrees in a tool's home (`~\.npu-agent\...`, app data) or used by a scheduled task are never offered |
| Package caches: Gradle, NuGet, npm, Yarn, pnpm, pip, Maven, Cargo, Go | the tools' own cache folders | never. Blocked while the tool runs (Java for Gradle, dotnet or Visual Studio for NuGet) |
| Android emulators and system images | the AVD folder and the SDK's `system-images` | system images no emulator uses. Emulators themselves aren't ticked, since they hold app data. Blocked while an emulator runs |
| Temp files and crash dumps | `%TEMP%` entries untouched for 7 days (`tempOlderThanDays`), `%LOCALAPPDATA%\CrashDumps` | yes |

Deletion never follows a link (pnpm's `node_modules` are full of junctions into its store), leaves files in use alone, and re-checks each item just before deleting it. `vdf-agent dev` shows the last check; `vdf-agent dev --scan` checks now. Set `"developerMode": "off"` to turn it off.

**Merged branches.** Each repository with a remote gets a **Prune** button:
- It fetches first (`git fetch --prune`), then deletes the local branches already merged into the remote's default branch (`origin/HEAD`, else `main` or `master`).
- It never deletes `main`, `master`, `develop`, `dev`, `trunk` or a branch checked out in any worktree.
- It uses `git branch -d`; `-D` only when git objects that the branch isn't merged into the current checkout, after re-checking that it is in the remote's default branch.
- Branches on the remote are never touched.

From a terminal: `vdf-agent dev --prune-branches <repo>`.

## Sharing the NPU

Other NPU tools on the PC can use the NPU at the same time, for example npu-agent's maintenance jobs, which run a local LLM on the NPU. The agent takes the same machine-wide lock they use (`%USERPROFILE%\.npu-agent\locks\npu`), when it exists:
- it holds the lock for at most 2 seconds at a time, so the other tool never waits longer than that;
- a stuck holder is evicted after 10 minutes.

## Settings

`%LOCALAPPDATA%\VDF Agent\agent.json`. Every field has a default.

| Field | Default | |
|---|---|---|
| `scanAllDrives` | true | Every fixed drive, minus the folders above |
| `folders` | none | More folders to scan, e.g. a USB drive or `\\nas\photos` (the only ones when `scanAllDrives` is false) |
| `excludeFolders` | none | A path (`D:\Scans`), a folder name at any depth (`Backups`), or either with wildcards (`D:\Old\*`, `*.bak`) |
| `excludeExtensions` | none | e.g. `[".heic"]` |
| `aiDevice` | `auto` | `auto` (NPU, else CPU), `npu`, `gpu`, `cpu` |
| `scanEveryMinutes` | 60 with an NPU, 360 on a GPU or CPU | `0`: no scheduled scans, only "Scan now". Only new and changed files are processed |
| `scanOnBattery`, `minBatteryPercent` | true, 30 | |
| `openPageAtSignIn` | true | Once a day, only when something waits for review |
| `port` | 18484 | The review page, on 127.0.0.1 only |
| `toast` | true | A notification when a scan finds something new |
| `developerMode` | `auto` | `off`: no developer cleanup. `auto`: check once a day |
| `staleProjectDays`, `tempOlderThanDays` | 30, 7 | When build outputs and temp files are ticked |

## Commands

```
vdf-agent                 install (or, once installed, open the review page)
vdf-agent scan [--open]   scan now
vdf-agent open            open the review page
vdf-agent status          settings, last scan, schedule, NPU lock
vdf-agent scope [--count] what a scan looks at and leaves out
vdf-agent dev [--scan]    developer mode: build outputs, worktrees, caches, emulators, temp
vdf-agent dev --prune-branches <repo>   delete local branches merged into the remote's main/master
vdf-agent setup           download FFmpeg and the AI components
vdf-agent install         [--dry-run] [--yes] [--device npu|gpu|cpu]
vdf-agent uninstall       [--purge] [--dry-run]
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

Folder names are read live from disk; counts come from the last scan (`index.json` next to the report).

## The review page is local only

- It listens on 127.0.0.1 and answers only its own Host header, so a DNS-rebinding page can't reach it.
- Every button needs a token that exists only inside the page it served, plus a same-origin Origin header.
- Thumbnails are served only for files in the current report.
- It stops after an hour unused.

## Build

```
dotnet build VDF.Agent -c Release
dotnet test VDF.Agent.Tests -c Release
dotnet publish VDF.Agent -c Release -r win-arm64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
```

Use `-r win-x64` for Intel and AMD PCs. The single file is about 50 MB. Set `VDF_AGENT_HOME` to keep a test copy's settings and report somewhere else.
