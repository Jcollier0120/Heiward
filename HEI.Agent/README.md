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
- very low disk priority, as the search indexer has, so anything else using the drive goes first.

A scan you start with **Scan now**, and a scheduled one while the review page is open, runs at full speed instead: every core but one, at normal priority, and normal disk priority. Open the page during a background scan and it speeds up; close it and a scheduled scan steps back. To keep every scan in the background, turn off **Full speed when you're here** in the page's Settings (`"scanSpeed": "background"`).

What a rescan reads from the disk:
- **Only what changed, as the drive's change journal says.** NTFS records every file created, changed, renamed or deleted, and Heiward reads that record as a normal user. That tells it which folders changed since the last scan.
  - **Changes Heiward ignores:** those in places scans don't look (Windows, programs, app data, code repositories, your exclusions).
  - **Folders where photos and videos may have changed** are listed again, one by one, and compared with the last listing. A document saved in Documents changes nothing.
  - **A drive with nothing new** isn't read at all. When no drive has anything new, a scheduled scan doesn't run: nothing is listed, compared or written, and a sleeping hard disk stays asleep. The log says `scan skipped, nothing new`.
  - **The drive is walked** as before, folder by folder, when the journal can't vouch for the last listing:
    - the first scan;
    - drives without a journal (FAT, exFAT, network drives);
    - a journal made again, or overwritten past the last scan (a PC off for a long time, or a very busy drive);
    - changed settings or a new build of Heiward;
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
5. opens the review page in your browser once a day at sign-in, and only when something waits for review;
6. adds **Heiward** shortcuts to the Start menu and the desktop (they open the review page in your default browser: a new tab if it's open, a new window if not, starting Heiward if needed), registers the name and icon its notifications show and `heiward:` links (the review page's "Start Heiward"), and adds an entry in Apps & Features so Windows can uninstall it.

</details>

**Already downloaded?** If another copy of Heiward already has FFmpeg and the AI components, the installer copies them instead of downloading: `hei install --reuse-from <that copy's folder>`. It also looks in the folder it was started from. A copy is kept only if it passes the same check as a download, and the NPU pack only if the model then really runs on the NPU.

**Updating:** run the new version's exe; it installs over the old one and scans again. Each report records the build that made it (its version and commit), because the sets are that build's rules. A report from another build is set aside, even under the same version number: the page shows none of its sets and nothing is cleaned up from them. When the page starts, it scans again with the new build unless scans are paused. The same goes for the developer report, and for the Microsoft Store version, which updates by itself. Sets the old report already listed aren't announced as new.

Unattended: `hei install --yes --device gpu` (add `--on-demand` for no scheduled scans, and `--scan-speed background` or `full` for how hard scans work). Preview every step without changing anything: `hei install --dry-run`.

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

Once you trust what the page ticks, you can let Heiward clean it up by itself. The **Automatic cleanup** card in the review page's Settings (the gear in the title bar) has a switch for duplicates and one for developer leftovers, both off until you turn them on (or `hei auto --duplicates on --developer on`).

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

**On the page:** right-click a folder (or press the menu key on it) and choose **Include in scans** or **Leave out of scans**, whichever it isn't now. An exempt folder's page has the same button. It edits the same two lists:
- leaving a folder out adds it to `excludeFolders`, or takes it out of `folders` if that's where it came from;
- including one takes its own path out of `excludeFolders`, or adds it to `folders` if a built-in rule left it out.

If a wider rule of yours covers it (`Old*`, or a folder above it), the page asks before removing that rule, since the rule covers other folders too. A whole drive can't be left out this way. The next scan follows.

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
| Git worktrees | the repository's `.git\worktrees` | untouched for 30 days, no uncommitted changes, and every commit already on a remote. Git removes it (`git worktree remove`, which refuses a worktree with changes), and the branch stays. Worktrees in a tool's home (a dot-folder such as `~\.<tool>\...`, or app data) or used by a scheduled task are never offered |
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

Other NPU tools on this PC can use the NPU at the same time. Heiward takes turns with the ones that use the same machine-wide lock, `%USERPROFILE%\.npu-agent\locks\npu` (the folder name is historical, kept so every tool still finds the lock), whenever `%USERPROFILE%\.npu-agent` exists:
- it holds the lock for at most 2 seconds at a time, so the other tool never waits longer than that;
- a stuck holder is evicted after 10 minutes.

## Settings

`%LOCALAPPDATA%\Heiward\settings.json`. Every field has a default.

| Field | Default | |
|---|---|---|
| `scanAllDrives` | true | Every fixed drive, minus the folders above |
| `folders` | none | More folders to scan, e.g. a USB drive or `\\nas\photos` (the only ones when `scanAllDrives` is false). Scanned even inside a folder left out by default |
| `excludeFolders` | none | A path (`D:\Scans`), a folder name at any depth (`Backups`), or either with wildcards (`D:\Old\*`, `*.bak`). Wins over `folders` |
| `onRequestDrives` | none | Drives scanned only when you ask (`D:\`): right-click a drive on the page, **Scan only when I ask**. Scheduled scans, the home page's Scan now, automatic cleanup and the developer check leave them alone, not reading them at all, so an archive disk can sleep. Their photos and videos as their last scan found them still count: their sets stay listed, and a copy of one elsewhere is still found. Scan one from its own page (**Scan this drive now**, or `hei scan --drive D:\`) |
| `excludeExtensions` | none | e.g. `[".heic"]` |
| `aiDevice` | `auto` | `auto` (NPU, else CPU), `npu`, `gpu`, `cpu` |
| `scanEveryMinutes` | 60 with an NPU, 360 on a GPU or CPU | `0`: no scheduled scans, only "Scan now". Only new and changed files are processed |
| `scanOnBattery`, `minBatteryPercent` | true, 30 | |
| `scanSpeed` | `auto` | `auto`: Scan now, and scheduled scans while the review page is open, at full speed; other scans in the background. `background`: every scan in the background. `full`: every scan at full speed, scheduled ones too |
| `parallelism` | 0 | Files decoded at once; 0: every core but one at full speed, half of them in the background |
| `backgroundCpuPercent` | 0 | The most of the processor a background scan uses, in percent, the FFmpeg it starts included; 0: a quarter, and at most two cores' worth |
| `keepHistory` | true | `false`: the page's History lists nothing new and keeps no file names; `heiward.log` leaves out developer paths and branch names too |
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
hei stop            stop the scan that's running
hei pause           pause scheduled scans and stop the running one  [--minutes N] (without it: until resumed)
hei resume          resume scheduled scans
hei open            open the review page
hei status          settings, where AI matching runs, last scan, schedule, NPU lock
hei scope [--count] what a scan looks at and leaves out
hei dev [--scan]    developer mode: build outputs, worktrees, caches, emulators, temp
hei dev --prune-branches <repo>   delete local branches merged into the remote's main/master
hei auto            automatic cleanup: what's due and when  [--duplicates on|off] [--developer on|off] [--after-days N]
hei setup           get FFmpeg and the AI components  [--reuse-from <folder>]
hei install         [--dry-run] [--yes] [--device npu|gpu|cpu] [--on-demand] [--scan-speed background|full|auto] [--no-browser] [--remove-github-copy] [--reuse-from <folder>]
hei uninstall       [--purge] [--dry-run]
```

## The review page

It's laid out like File Explorer, so you can go where you care most instead of scrolling every duplicate on the PC:

- **This PC:** a card per drive with its free space, how many photos and videos it holds, how long its last scan took, and its sets of copies and space to free. Below the cards, the folders where cleaning up frees the most, and the history of what you've done:
  - A folder's cleanup, or its **Skip all**, is one line.
  - **Clear history** empties the list and forgets the file names in it. Sets you kept stay hidden, and "freed so far" stays.
  - **Keep a history** off (in Settings) lists nothing new.
- **A folder:** the navigation tree on the left and the folder on the right:
  - Its subfolders in a details view you can sort by space to free, with only the duplicates that touch this folder below.
  - Copies and look-alikes are shown separately.
  - A copy kept in another folder is dimmed and says so.
  - One button moves every ticked copy in the folder to the Recycle Bin, keeping the kept file of each set.
- **Exempt folders** (system, programs, games, code, other accounts) are greyed out with the reason. Many of them together fold into one row. Folders without photos or videos are hidden behind a "show" link.

**Where AI matching runs:** a badge in the title bar. It's green ("NPU ready", then "Running on the NPU" once a scan has used it). Otherwise it names the device and why: "No NPU available", "Unsupported NPU" (an NPU this version can't drive yet), "NPU not set up" (its pack isn't downloaded), or "NPU fell back" (it couldn't run the model; `heiward.log` says why). The install, `hei setup` and every scan write this to `ai-status.json`, so the page reads one small file and is right from the first visit.

**Scanning on its own:** the title bar has the controls, and a banner says when Heiward isn't scanning by itself.
- **Scan now** turns into **Stop scan** while a scan runs: the scan stops within a second, without a report (`hei stop`).
- **Pause** stops the scan that's running, and scheduled scans skip themselves: for an hour, 4 hours, until tomorrow morning, or until you resume (`hei pause`, `hei resume`). Scan now still works while paused.
- **Its scan task gone or turned off** in Task Scheduler: **Turn them back on** registers it again.
- **Heiward not running** (it stopped, or the PC slept): the page stays as it was, says so, and offers **Start Heiward**, a `heiward://start` link the installer registers. Once Heiward is back, the page reloads by itself.

**Settings:** the gear in the title bar opens every switch in one place: **Scans run** (in the background, at full speed when you're here, or always at full speed), **Automatic cleanup**, and **Keep a history** (with Clear history). It also shows what the settings file sets that the page has no switch for (what's scanned, skipped file types, where AI matching runs), and where the file is.

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

The icon (`heiward.ico`, and `wwwroot\heiward.png` for notifications) is rendered from `wwwroot\favicon.svg` by `make-icon.ps1`; run it again after changing the mark.

### Making a release

From the repository root, on the commit to release, with the .NET 10 SDK and the GitHub CLI (`gh`, signed in) on PATH:

```
powershell -ExecutionPolicy Bypass -File HEI.Agent\release.ps1 -Publish
```

It builds `Heiward-<version>-x64.exe` and `Heiward-<version>-arm64.exe` into `artifacts\heiward`, writes `SHA256SUMS.txt`, then creates the release `v<version>` at the commit it built and uploads all three. The version is `VersionPrefix` in `HEI.Agent.csproj`. It refuses to publish with uncommitted changes, or when that release already exists.

- Without `-Publish` it only builds.
- `-Notes <text or file>` replaces the default release notes, which say which file to download and how to get past the unsigned-build warning.

GitHub attaches the source code to every release by itself.
