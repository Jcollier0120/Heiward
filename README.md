# Heiward

Heiward keeps your Windows PC tidy in the background. It finds duplicate photos and videos and, in developer mode, stale developer files, then lists them on a local review page. Nothing is removed until you say so, and files you tick go to the Recycle Bin.

- **Light on power.** AI matching runs on the Snapdragon NPU where there is one, with hourly scans. Otherwise it uses the GPU or CPU every 6 hours on AC power, or only when you ask. Scheduled scans run in Windows' efficiency mode.
- **The whole PC, minus what isn't yours.** Every fixed drive, leaving out Windows, programs, games, app data and code repositories. Cloud-only files are never downloaded.
- **Laid out like File Explorer.** A card per drive, a folder tree, and the duplicates of the folder you're in. Only identical files and pixel-level copies are ticked for you.
- **Developer cleanup, project by project.** Build outputs, finished worktrees, package caches, unused emulator images, old temp files, and a button that prunes local branches already merged into main.
- **Themes.** Match Windows, Light, Dark, and six colour themes.

A *heiward* (Middle English, "hedge warden") was the village officer who kept the hedges trimmed and the fences sound.

## Install

1. Download `hei.exe` from [Releases](https://github.com/Jcollier0120/Heiward/releases).
2. Run it.

There's nothing to install first: no .NET, no FFmpeg, no admin rights. Heiward downloads what it needs itself and runs its first scan. On a PC without an NPU it asks one question: run the AI on the graphics card or the processor.

To remove it, uninstall **Heiward** in Settings > Apps.

The first release isn't out yet. Until then, [build it](VDF.Agent/README.md#build). Settings, commands and how it decides what to tick are in [VDF.Agent/README.md](VDF.Agent/README.md).

## Based on Video Duplicate Finder

Heiward is built on [Video Duplicate Finder](https://github.com/0x90d/videoduplicatefinder), a cross-platform duplicate finder with a desktop app, a command line, a web UI and a Docker image. This repository is a fork of it, and those still build from here; their documentation is [upstream's README](https://github.com/0x90d/videoduplicatefinder#readme).

What the fork adds to Video Duplicate Finder's engine:
- AI matching on a Snapdragon **NPU** (Qualcomm QNN) or a **DirectML GPU**, as well as the CPU, with a machine-wide NPU lock.
- Photos decode in-process: through **WIC** on Windows, and iPhone HEIC photos through one in-process FFmpeg decoder instead of one `ffmpeg.exe` per photo.
- One gray-frame rule for every decoder and platform, so a HEIC and its JPEG export are recognised as copies.
- **Windows ARM64:** faster comparisons (NEON, SDOT), a passing test suite, and a pinned FFmpeg build that loads on Snapdragon X.
- Scan-scope fixes: wildcard folder patterns on Windows paths, folder-name excludes at any depth, and skipping folder links and repositories.

# License
Video Duplicate Finder is licensed under AGPLv3.

Heiward (`VDF.Agent`) is AGPLv3 too. When it installs, it downloads FFmpeg, ONNX Runtime and the DINOv2 model, plus Qualcomm's QNN runtime on Snapdragon PCs or DirectML for a GPU. Each carries its own licence.

The optional AI components are downloaded separately on first use and carry their own licenses: ONNX Runtime (MIT) and the DINOv2-small embedding model (Apache-2.0). Neither is bundled with or linked into the release binaries.

# Credits / Third Party
- [Avalonia](https://github.com/AvaloniaUI/Avalonia)
- [ActiPro Avalonia Controls (Free Edition)](https://github.com/Actipro/Avalonia-Controls)
- [FFmpeg.AutoGen](https://github.com/Ruslan-B/FFmpeg.AutoGen)
- [MemoryPack](https://github.com/Cysharp/MemoryPack)

- [AcoustID.NET by wo80](https://github.com/wo80/AcoustID.NET) — the audio fingerprinting pipeline (Chromaprint-style chroma extraction, FIR smoothing, and fingerprint encoding) used for partial clip detection is derived from this library, licensed under LGPL 2.1

- [ONNX Runtime](https://github.com/microsoft/onnxruntime) (Microsoft, MIT) — inference engine for the optional AI matching feature
- [DINOv2](https://github.com/facebookresearch/dinov2) (Meta AI, Apache-2.0) — the image embedding model behind AI matching, used as the int8-quantized ONNX export from [Xenova/dinov2-small](https://huggingface.co/Xenova/dinov2-small) (mirrored on this repo's [ai-models-v1 release](https://github.com/0x90d/videoduplicatefinder/releases/tag/ai-models-v1))
