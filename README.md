# Heiward

Heiward is NPU-first AI for Windows. It runs a vision model, DINOv2, on the PC's NPU (the AI processor in Snapdragon X, Intel Core Ultra and AMD Ryzen AI PCs) to find duplicate photos and videos, including resized, recompressed, cropped, mirrored and edited copies. On the NPU it's fast and barely uses power, so it can scan every hour in the background. All of it runs on your PC: nothing about you or your files leaves it.

It lists what it finds, and stale developer files in developer mode, on a local review page. Nothing is removed until you say so, and files you tick go to the Recycle Bin. Once you trust what it suggests, it can clean up by itself.

- **NPU first.** Heiward detects the PC's NPU and downloads that vendor's runtime: Qualcomm QNN, Intel OpenVINO, or AMD Vitis AI through Windows ML. A badge in the title bar shows where the AI runs, and why not on the NPU. Without one it falls back to the GPU or CPU, every 6 hours on AC power or only when you ask. It takes turns with other NPU tools on the PC that use the same lock.
- **Light on power.** Scheduled scans run in Windows' efficiency mode, with a cap on the processor. Scan now, and any scan while the page is open, runs at full speed.
- **The whole PC, minus what isn't yours.** Every fixed drive, leaving out Windows, programs, games, app data and code repositories. Cloud-only files are never downloaded. Right-click a folder on the page to include it or leave it out, or a drive to scan it only when you ask, so an archive disk can sleep.
- **Laid out like File Explorer.** A card per drive, a folder tree, and the duplicates of the folder you're in. Only identical files and pixel-level copies are ticked for you. Burst shots (`IMG_1234`, `IMG_1235`) and pictures less than 75% alike aren't offered as look-alikes, and a folder's look-alikes can be skipped all at once.
- **A Developer area for all your repositories at once.** Each repository's open pull requests, from GitHub, Azure DevOps and TFS, GitLab, Bitbucket, Gitea or Forgejo, beside its cleanup, with those ready to merge or waiting for a review first. The cleanup is its build outputs, finished worktrees, and a button that prunes local branches already merged into main, plus package caches, unused emulator images and old temp files. Git, TFVC, Subversion, Mercurial, Perforce and other version control are recognised. It's off until you turn on Developer mode in Settings, or, with Manor installed, Manor's Developer options.
- **Automatic cleanup, when you're ready.** Two switches let it clean plain copies and developer leftovers by itself, a few days after listing them, with a "Leave it" button on each. Edits, look-alikes, cloud-synced copies and anything that looks like a backup always wait for you.
- **Themes.** Match Windows, Light, Dark, and six colour themes.

A *heiward* (Middle English, "hedge warden") was the village officer who kept the hedges trimmed and the fences sound.

![Heiward's home page: a card per drive or folder with its sets of copies, developer cleanup, and the folders where cleaning up frees the most](docs/screenshots/home.png)

<table>
<tr>
<td width="50%"><img src="docs/screenshots/duplicates.png" alt="A folder's page: its subfolders, and its duplicates with the copy to remove ticked"></td>
<td width="50%"><img src="docs/screenshots/developer.png" alt="Developer cleanup in the Carbon theme: projects, shared caches, and what's selected for cleaning"></td>
</tr>
<tr>
<td>A folder and its duplicates. Each set keeps one file; the copies go to the Recycle Bin.</td>
<td>Developer cleanup, project by project (Carbon theme).</td>
</tr>
<tr>
<td width="50%"><img src="docs/screenshots/themes.png" alt="The theme menu over the Quest theme"></td>
<td>Match Windows, Light, Dark, or one of six colour themes (Quest here).<br><br>The dogs are the author's own. The developer projects are made up.</td>
</tr>
</table>

## Install

1. Download from [Releases](https://github.com/Jcollier0120/Heiward/releases):
   - `Heiward-<version>-x64.exe` for most PCs (Intel or AMD);
   - `Heiward-<version>-arm64.exe` for Arm PCs, such as Snapdragon Copilot+ PCs.

   Not sure which? **Settings > System > About** says "x64-based processor" or "ARM-based processor".
2. Run it.

There's nothing to install first: no .NET, no FFmpeg, no admin rights. Heiward downloads what it needs itself and runs its first scan, and the review page opens in your browser to show its progress. It asks how hard scans should work: in the background, slower and light on power, or at full speed. On a PC without an NPU it also asks whether to run the AI on the graphics card or the processor.

The releases aren't code-signed yet, so the first run shows "Windows protected your PC": click **More info**, then **Run anyway**. On a PC with Smart App Control turned on, Windows blocks unsigned apps outright, so those PCs will need a signed release. `SHA256SUMS.txt` on the release lists each file's SHA-256, which PowerShell's `Get-FileHash` shows.

To remove it, uninstall **Heiward** in Settings > Apps. Its window says when it's done, or what stopped it; either way the steps are in `%LOCALAPPDATA%\Heiward\heiward.log`. Your settings and history stay there, for a later install (`hei uninstall --purge` deletes them too).

To build it yourself, see [Build](HEI.Agent/README.md#build). Settings, commands and how it decides what to tick are in [HEI.Agent/README.md](HEI.Agent/README.md).

## Where its work runs

The review page's Settings says the same, for the PC it runs on: where the model runs there, the schedule, and how hard scans work.

- **The model.** AI matching compares pictures and video frames with DINOv2, a vision model. It runs on the NPU, through its maker's runtime (Qualcomm's QNN, Intel's OpenVINO or AMD's Vitis AI, with Windows ML); without an NPU that works, on a graphics card through DirectML; otherwise on the processor. The badge in the title bar says which.
- **Video frames.** FFmpeg decodes the frames Heiward samples from each video. A few videos at a time go to the graphics chip's video decoder, which costs the processor little; the rest, and any it can't decode, are decoded on the processor. iPhone photos use that decoder too, beside the processor.
- **Files.** Listing folders, reading files and hashing them is processor and disk work. A rescan reads only new and changed files. Files of the same size are hashed whole, once, to find exact copies; photos are decoded, and the sound of videos fingerprinted, on the processor.
- **When it runs.** Every hour on a PC with an NPU; without one, every 6 hours on AC power, or only when you press Scan now, as you chose when installing. A scan with nothing new is a quick look at the folders. In the background, a scan runs in Windows' efficiency mode, at low priority and at most a quarter of the processor (two cores' worth at most); at full speed it uses every core but one. Settings' "Scans run" picks which: with "at full speed when you're here", Scan now and any scan while the review page is open run at full speed, and scheduled ones in the background. While a game or anything full screen runs, scans make way.
- **Taking turns.** On the NPU, the model takes its turn in the queue the manor's agents on the PC share: Heiward holds the NPU for at most 2 seconds, then joins the back of the line, and work someone is waiting on goes first. On a graphics card it takes that card's turn the same way.
- **Why the processor and graphics come and go.** A scan comes in bursts: the folders, then decoding and comparing what's new, then hashing, and between scans Heiward does nothing. The graphics chip is busy only while it decodes videos or runs the model, the NPU in short turns. A scan in the background stays under its cap; one at full speed can fill the processor until it finishes.

# License
Heiward is free software under the GNU AGPL v3 ([LICENSE](LICENSE)).

It includes code from [Video Duplicate Finder](https://github.com/0x90d/videoduplicatefinder) (© 0x90d and contributors), also under the AGPL v3: the scan engine (`HEI.Core`), and the desktop app, command line and web UI this repository still builds (`HEI.GUI`, `HEI.CLI`, `HEI.Web`; their documentation is [Video Duplicate Finder's README](https://github.com/0x90d/videoduplicatefinder#readme)).

When Heiward installs, it downloads FFmpeg, ONNX Runtime and the DINOv2 model, plus the NPU runtime for the PC's NPU (Qualcomm QNN, Intel OpenVINO, or AMD Vitis AI, which Windows ML supplies) or DirectML for a GPU. Each carries its own licence.

The optional AI components are downloaded separately on first use and carry their own licenses: ONNX Runtime (MIT) and the DINOv2-small embedding model (Apache-2.0). Neither is bundled with or linked into the release binaries.

# Credits / Third Party
- [Avalonia](https://github.com/AvaloniaUI/Avalonia)
- [ActiPro Avalonia Controls (Free Edition)](https://github.com/Actipro/Avalonia-Controls)
- [FFmpeg.AutoGen](https://github.com/Ruslan-B/FFmpeg.AutoGen)
- [MemoryPack](https://github.com/Cysharp/MemoryPack)

- [AcoustID.NET by wo80](https://github.com/wo80/AcoustID.NET) — the audio fingerprinting pipeline (Chromaprint-style chroma extraction, FIR smoothing, and fingerprint encoding) used for partial clip detection is derived from this library, licensed under LGPL 2.1

- [ONNX Runtime](https://github.com/microsoft/onnxruntime) (Microsoft, MIT) — inference engine for the optional AI matching feature
- [DINOv2](https://github.com/facebookresearch/dinov2) (Meta AI, Apache-2.0) — the image embedding model behind AI matching, used as the int8-quantized ONNX export from [Xenova/dinov2-small](https://huggingface.co/Xenova/dinov2-small) (mirrored on this repo's [ai-models-v1 release](https://github.com/0x90d/videoduplicatefinder/releases/tag/ai-models-v1))
