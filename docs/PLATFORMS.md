# Heiward on Linux and macOS: the plan

Heiward is Windows-only today. This is what a Linux and a macOS version take, in the order to build them.

## Already portable

- **The engine** (`VDF.Core`) targets `net10.0`, and upstream Video Duplicate Finder ships it for Linux and macOS.
- **Downloads.** FFmpeg already downloads for Linux (x64, Arm64) and macOS (Intel, Apple Silicon), and ONNX Runtime for all four.
- **Photos.** They decode through FFmpeg in-process, iPhone HEIC included. WIC is only a Windows speed-up.
- **The review page** is ASP.NET Core plus plain HTML and JavaScript. Opening it uses `xdg-open` or `open`, which .NET already calls for a URL.
- **Most other code:** the report, scan index, folder tree, decisions, and developer mode's logic. Git and deletion that never follows links (`LinkTarget`) work on every OS.

## Windows-only today

About 680 of `VDF.Agent`'s 3,700 lines, plus Windows specifics inside the scan scope and developer mode:

| Part | Windows | Linux | macOS |
|---|---|---|---|
| Project | `net10.0-windows` | `net10.0`, with the Windows parts behind `OperatingSystem.IsWindows()` | same |
| Install folder | `%LOCALAPPDATA%\Programs\Heiward` | `~/.local/bin/hei`, state in `~/.local/share/heiward` | `~/.local/bin/hei`, state in `~/Library/Application Support/Heiward` |
| Scheduled scans | Task Scheduler | systemd user timer and service | launchd LaunchAgent (`StartInterval`) |
| Page at sign-in | logon task | `~/.config/autostart/heiward.desktop` | LaunchAgent with `RunAtLoad` |
| App menu | Start menu `.lnk` | `~/.local/share/applications/heiward.desktop` | none at first (an `.app` wrapper later) |
| Uninstall | Apps & Features | `hei uninstall` | `hei uninstall` |
| Notifications | toast under a registered AppUserModelID | `notify-send` / D-Bus | `osascript` at first; a proper sender needs a signed `.app` |
| Recycle Bin | Shell API | freedesktop Trash spec (`~/.local/share/Trash`, `.Trash-$UID` per mount) | `NSFileManager trashItemAtURL` through the Objective-C runtime |
| Cloud-only files | Cloud Files attributes | none to skip | iCloud Drive "dataless" files (`st_flags & SF_DATALESS`) |
| Scan scope | fixed drives minus Windows, programs, games, AppData | mounts from `/proc/self/mountinfo`, minus pseudo file systems, `/usr` `/etc` `/var` `/opt` `/snap` `/nix`, `~/.cache`, the Trash, Steam and Flatpak | `/` and `/Volumes/*`, minus `/System` `/Library` `/Applications` `/private` and `~/Library` |
| Efficiency mode | EcoQoS | `nice` 10 plus idle I/O priority (`ioprio_set`) | LaunchAgent `ProcessType Background` and `LowPriorityIO` |
| Battery | `GetSystemPowerStatus` | `/sys/class/power_supply` | `pmset -g batt` |
| Developer mode | Windows cache and SDK paths | `~/.npm`, `~/.gradle`, `~/.nuget`, `~/.cache/pip`, `~/.cargo`, `~/go/pkg/mod`, `~/Android/Sdk`, `/tmp`, `coredumpctl` | the same plus Xcode `DerivedData`, unused iOS simulators (`xcrun simctl delete unavailable`), Archives, CocoaPods and Homebrew caches, `~/Library/Android/sdk`, `~/Library/Logs/DiagnosticReports` |
| NPU | Qualcomm, Intel, AMD packs | Intel's OpenVINO pack (it ships Linux x64 files; needs Intel's NPU driver); CPU otherwise | the Apple Neural Engine through ONNX Runtime's CoreML provider (in the macOS runtime already): `MLComputeUnits=CPUAndNeuralEngine`, `MLProgram`, static shapes |
| Release files | `Heiward-<v>-x64.exe`, `-arm64.exe` | `heiward-<v>-linux-x64.tar.gz`, `-linux-arm64` | `heiward-<v>-macos-arm64.tar.gz` (and Intel if wanted) |

## Order and effort

1. **Groundwork, 1–2 days, testable on Windows.**
   - Put the Windows parts behind small interfaces: scheduler, trash, notifier, scope rules, power, paths.
   - Keep the Windows implementations unchanged, moved rather than rewritten.
   - Switch the project to `net10.0`, and cross-publish `linux-x64` and `osx-arm64` from Windows to prove they build.
2. **Linux, about 1–1.5 weeks with testing.**
   - The implementations above, with `release.ps1` publishing the Linux files.
   - Testing needs WSL2 (`wsl --install`, which needs admin rights and a restart, with systemd enabled) or a Linux PC.
   - Distro packages (AppImage, `.deb`, Flatpak) come later; a tarball and `hei install` come first.
3. **macOS, about 1.5–2 weeks with testing.**
   - The implementations, plus the Neural Engine, iCloud's dataless files, and privacy prompts (Full Disk Access for Photos, Desktop and Documents).
   - Signing and notarization: without them, Gatekeeper blocks a browser download. They need the Apple Developer Program ($99 a year). `rcodesign` can sign and notarize from Windows or Linux; the rest needs a Mac, ideally Apple Silicon.

## Test hardware still needed

- **Linux:** WSL2 on this PC, or any Linux PC.
- **macOS:** a Mac with Apple Silicon.
- **Windows NPUs:**
  - An Intel Core Ultra PC: install and scan, nothing else. An older Intel PC has no NPU, so Heiward rightly uses its GPU or CPU; setting `VDF_NPU_TEST=openvino-cpu` makes it run the Intel pack on the CPU instead, which tests everything but the NPU itself.
  - An AMD Ryzen AI PC on Windows 11 24H2 or later: install and scan. The Windows ML route it uses is tested on a Snapdragon PC with `VDF_NPU_TEST=winml-qnn` (same 82 groups on the 600-photo test set as Qualcomm's own pack).
