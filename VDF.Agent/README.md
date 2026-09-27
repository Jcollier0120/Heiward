# VDF Agent: duplicate check in the background

`vdf-agent` looks through your photo and video folders for likely duplicates and lists them on a local review page. It never deletes anything on its own. Files you tick go to the Recycle Bin, where you can restore them.

It is built on Video Duplicate Finder's engine. The AI matching finds resized, recompressed, cropped, mirrored and edited copies, and runs on:
- the **NPU** of a Snapdragon PC: fast, and it barely uses power, so scans can run every hour;
- your **GPU** (any DirectX 12 GPU, through DirectML), if you choose it;
- the **CPU**.

## Install

Download `vdf-agent.exe` and run it. It is one self-contained file: no .NET, no admin rights. It then:

1. copies itself to `%LOCALAPPDATA%\Programs\VDF Agent`;
2. downloads FFmpeg, ONNX Runtime and the DINOv2 model, plus the NPU pack on Snapdragon PCs. Every download is SHA-256 pinned;
3. checks for an NPU. Without one, it asks whether the AI should run on the **GPU** or the **CPU**;
4. schedules scans with Task Scheduler (per user). With an NPU or GPU they run every hour, and on battery they step aside in Battery Saver or below 30%. On the CPU they run once a day;
5. opens the review page in your browser once a day at sign-in, and only when something waits for review;
6. adds **Duplicate check** to the Start menu, and an entry in Apps & Features so Windows can uninstall it.

Unattended: `vdf-agent install --yes --device gpu`. Preview every step without changing anything: `vdf-agent install --dry-run`.

The agent does not change your browser's startup pages. Browsers protect those, and changing them is what browser hijackers do. The sign-in step opens a normal tab instead.

## What it decides, and what it leaves to you

Code decides everything shown on the page. No model output is trusted to delete anything.

| On the page | Meaning | Ticked for the Recycle Bin? |
|---|---|---|
| Identical copy | Byte-for-byte the same file (SHA-256) | yes |
| Smaller copy / More compressed copy / Saved again | The same picture pixel for pixel (grayscale match ≥ 99.5%), at a lower resolution or more compressed | yes |
| Edited version | The AI sees the same picture with colours, a filter or a flip changed (≥ 97%) | no |
| Edited, cropped, flipped, or a similar shot | Crops, flips, and different shots that look alike, such as bursts | no |

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

## Sharing the NPU

Other NPU tools on the PC can use the NPU at the same time, for example npu-agent's maintenance jobs, which run a local LLM on the NPU. The agent takes the same machine-wide lock they use (`%USERPROFILE%\.npu-agent\locks\npu`), when it exists:
- it holds the lock for at most 2 seconds at a time, so the other tool never waits longer than that;
- a stuck holder is evicted after 10 minutes.

## Settings

`%LOCALAPPDATA%\VDF Agent\agent.json`. Every field has a default.

| Field | Default | |
|---|---|---|
| `folders` | Pictures, Videos, Desktop, iCloud Photos, Downloads | Scanned with subfolders |
| `excludeFolders`, `excludeExtensions` | none | e.g. `[".heic"]` |
| `aiDevice` | `auto` | `auto` (NPU, else CPU), `npu`, `gpu`, `cpu` |
| `scanEveryMinutes` | 60 | Only new and changed files are processed |
| `scanOnBattery`, `minBatteryPercent` | true, 30 | |
| `openPageAtSignIn` | true | Once a day, only when something waits for review |
| `port` | 18484 | The review page, on 127.0.0.1 only |
| `toast` | true | A notification when a scan finds something new |

## Commands

```
vdf-agent                 install (or, once installed, open the review page)
vdf-agent scan [--open]   scan now
vdf-agent open            open the review page
vdf-agent status          settings, last scan, schedule, NPU lock
vdf-agent setup           download FFmpeg and the AI components
vdf-agent install         [--dry-run] [--yes] [--device npu|gpu|cpu]
vdf-agent uninstall       [--purge] [--dry-run]
```

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
