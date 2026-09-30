# Microsoft Store

In the Store, Heiward is an MSIX package that Microsoft signs after certification. People who install it from there get no "Windows protected your PC" prompt, and Smart App Control lets it run. Publishing it costs nothing: individual developer accounts are free.

[`HEI.Agent/store.ps1`](../HEI.Agent/store.ps1) builds the package: `Heiward-<version>.msixbundle` with an x64 and an arm64 package, in `artifacts\store`. Its manifest is [`HEI.Agent/Store/AppxManifest.xml`](../HEI.Agent/Store/AppxManifest.xml).

```
powershell -ExecutionPolicy Bypass -File HEI.Agent\store.ps1
```

## What's in the package

- **`hei.exe`** and the .NET runtime, published as separate files rather than one: the single-file exe from GitHub unpacks its native libraries into `%TEMP%` at startup, where the package's signature doesn't cover them.
- **`Heiward.exe`**, the Start menu entry. It's `hei.exe`'s .NET app host with the Windows GUI subsystem, so it runs `hei open` without a console window.
- **A desktop shortcut** (`desktop7:Shortcut`), which Windows adds and removes with the app, on Windows 11 (Windows 10 ignores it).
  - It starts `Heiward.exe` by its path, without the package's identity.
  - Heiward notices (its folder has an `AppxManifest.xml`) and hands over to the packaged app, as the Start menu starts it: `IApplicationActivationManager`, with the app ID worked out from the manifest's name and publisher.
  - It never takes itself for the GitHub download.
- **`heiward:` links** (`windows.protocol`): the review page's "Start Heiward", when it can't reach Heiward.
  - `heiward://start` starts it in the background, and the page reloads by itself.
  - Any other `heiward:` link opens the page.
- **The `hei` command** in a terminal, as an app execution alias. The alias's path, `%LOCALAPPDATA%\Microsoft\WindowsApps\hei.exe`, stays the same across updates while the package's own folder changes with every version. That makes it the path for the scheduled tasks.
- **FFmpeg** in `bin\`, where Heiward looks for it first. The Store signs it with the rest of the package, so Smart App Control lets it load, and nothing is downloaded after install. Its licenses and build notes are in `licenses\FFmpeg`.
  - It's the lean LGPL build from [ffmpeg-winarm64-lean](https://github.com/Jcollier0120/ffmpeg-winarm64-lean), pinned by release and SHA-256.
  - **arm64:** `n8.1.3-2`, the same one the GitHub exe downloads.
  - **x64:** `n8.1.3-3`, the same configuration built for x64.
- **Logos** in `Store\Assets`, rendered from `wwwroot\favicon.svg` by `make-icon.ps1`, and a `resources.pri` that lets Windows pick one per size.

## Partner Center

Heiward is registered under the publisher **The Nexus**, with Store ID `9NX5K0L4CW3D` and package family `TheNexus.Heiward_mcanr0hfqkj1g`. The manifest's identity (**Product management > Product identity**) is filled in.

### Releasing

1. **Build the bundle** from a clean checkout of `master`, with `store.ps1` (above). The package's version is `VersionPrefix` in `HEI.Agent.csproj` with a `.0`; every submission needs a higher one than the last.
2. **The first submission goes to a private audience.** The Store installs a flight only for an app that has a published submission, and an app published to everyone can't go back to private. So the first one is the test (**Pricing and availability > Visibility > Private audience**), with a known user group holding your personal Microsoft account's email address.
3. **Install it from the Store** with the link Partner Center gives for the private audience, and try it on each architecture you can: setup, a scan, Scan now, the desktop shortcut, a `heiward:` link, `hei` in a terminal, and a notification.
   - First remove any copy registered from a build folder (`Get-AppxPackage TheNexus.Heiward | Remove-AppxPackage`). It has the same identity, so the Store can't install next to it. Removing it deletes its AI components and setup marker: setup runs again, with the settings and history kept.
4. **Go public:** a new submission with **Public audience**. It may go through certification again.

### What each page asks

**Pricing and availability:** Free, in every market. Visibility as in step 2 above.

**Properties:**
- **Category:** Utilities & tools.
- **Privacy policy URL:** `https://github.com/Jcollier0120/Heiward/blob/master/docs/PRIVACY.md` ([PRIVACY.md](PRIVACY.md)). The Store requires one because Heiward reads the user's photos and videos.
- **Website:** `https://github.com/Jcollier0120/Heiward`
- **Support contact info:** `https://github.com/Jcollier0120/Heiward/issues`
- **System requirements:** Windows 10 version 2004 or later, on an x64 or Arm64 PC. An NPU (Qualcomm Snapdragon, Intel Core Ultra or AMD Ryzen AI) is recommended, not required.

**Age ratings:** the questionnaire's app category is the one for utilities and productivity apps, not games. Heiward has no user-to-user contact, no sharing of the user's location, no purchases and no web browsing, so every answer is No.

**Packages:** `artifacts\store\Heiward-<version>.msixbundle`.

**Store listings (English, United States):**
- **Description:**
  ```
  Heiward keeps your Windows PC tidy in the background. It finds duplicate photos and videos and, in developer mode, stale developer files, then lists them on a review page on your PC. Nothing is removed until you say so, and files you tick go to the Recycle Bin. Once you trust what it suggests, it can clean up by itself.

  Light on power. AI matching runs on the NPU where there is one (Qualcomm Snapdragon, Intel Core Ultra, or AMD Ryzen AI), with hourly scans. Otherwise it uses the graphics card or the processor, every 6 hours on AC power, or only when you ask. Scheduled scans run in Windows' efficiency mode.

  The whole PC, minus what isn't yours. Every fixed drive, leaving out Windows, programs, games, app data and code repositories. Cloud-only files are never downloaded. Right-click a folder on the page to include it or leave it out.

  Laid out like File Explorer. A card per drive, a folder tree, and the duplicates of the folder you're in. Only identical files and pixel-level copies are ticked for you. Burst shots and pictures less than 75% alike aren't offered as look-alikes.

  Developer cleanup, project by project. Build outputs, finished worktrees, package caches, unused emulator images, old temp files, and a button that prunes local branches already merged into main.

  Automatic cleanup, when you're ready. Two switches let it clean plain copies and developer leftovers by itself, a few days after listing them, with a "Leave it" button on each. Edits, look-alikes, cloud-synced copies and anything that looks like a backup always wait for you.

  Private. No account, no ads, no telemetry: nothing about you or your files leaves your PC.

  Heiward is free and open source (AGPL-3.0), built on Video Duplicate Finder. The source is at github.com/Jcollier0120/Heiward.
  ```
- **What's new in this version:** `The first release in the Microsoft Store.`
- **Product features** (one per line in Partner Center):
  ```
  Finds duplicate photos and videos on every fixed drive, including copies in other formats and sizes
  AI matching on the NPU, the graphics card or the processor
  A review page laid out like File Explorer: a card per drive, a folder tree, and each folder's duplicates
  Nothing is removed until you say so, and removed files go to the Recycle Bin
  Developer cleanup: build outputs, finished worktrees, package caches, emulator images and old temp files
  Optional automatic cleanup of plain copies and developer leftovers, with a "Leave it" button on each
  Scheduled scans in Windows' efficiency mode; Scan now runs at full speed
  Cloud-only files are never downloaded
  Match Windows, Light, Dark, and six colour themes
  No account and no telemetry: nothing leaves your PC
  ```
- **Screenshots** (2560 × 1600, from `docs/screenshots`), in this order, with these captions:
  1. `home.png`: `A card per drive or folder with its sets of copies, developer cleanup, and where cleaning up frees the most`
  2. `duplicates.png`: `A folder and its duplicates. Each set keeps one file; the copies go to the Recycle Bin`
  3. `developer.png`: `Developer cleanup, project by project (Carbon theme)`
  4. `themes.png`: `Match Windows, Light, Dark, or one of six colour themes (Quest here)`
- **Search terms** (up to 7): `duplicate photos`, `duplicate videos`, `duplicate finder`, `disk cleanup`, `photo cleanup`, `NPU`, `developer cleanup`.
- **Copyright and trademark info:** `© 2026 Jeremy Collier. Built on Video Duplicate Finder.`
- **Additional license terms:** `Heiward is free software under the GNU Affero General Public License v3: https://www.gnu.org/licenses/agpl-3.0.html. Its source code is at https://github.com/Jcollier0120/Heiward.`

**Submission options:**
- **Why it needs `runFullTrust`** (a restricted capability):
  ```
  Heiward is a desktop app (full trust). It scans the user's fixed drives for duplicate photos and videos, schedules its scans with Task Scheduler, moves the files the user picks to the Recycle Bin, and serves its review page to the user's browser on 127.0.0.1 only.
  ```
- **Notes for certification:**
  ```
  Heiward's window is a page in the default browser, served by Heiward on http://127.0.0.1:18484. No account or sign-in is needed.

  1. Start Heiward from the Start menu. The page opens with first-run setup.
  2. Pick where the AI runs (the NPU when the PC has one, otherwise the graphics card or the processor) and how hard scans work, then start. Setup downloads the AI components (ONNX Runtime and a small image model from GitHub and NuGet; about 215 MB more for the graphics card), so the PC needs internet access.
  3. The first scan starts by itself; its progress is on the page. With few photos on the PC it finds little: copy a photo to a second folder to see a set of duplicates. Tick a copy and move it to the Recycle Bin.
  4. "hei" in a terminal is the command line (hei --help).
  ```

## Trying a build locally

With Developer Mode on (**Settings > System > For developers**), register an unpacked package without signing it:

```
Add-AppxPackage -Register artifacts\store\layout-x64\AppxManifest.xml
```

Use `layout-arm64` on an Arm PC. To remove it: `Get-AppxPackage TheNexus.Heiward | Remove-AppxPackage`. Removing it deletes its storage (the AI components and the setup marker), so the next install runs setup again.

- **A new build of an installed copy:** stop its processes and copy the files over the registered folder. It runs the new files at once.
- **A changed manifest:** Windows re-registers it only under a higher version. Raise the fourth number in the registered copy's `AppxManifest.xml` (1.3.0.1). Store packages keep it at 0.

## How the Store version runs

`StorePackage.IsPackaged` tells Heiward it runs from the package: `GetCurrentPackageFamilyName` fails outside one.

- **First run:** it happens on the review page, because a Store install has no console (`StoreSetup`).
  - **The NPU:** when setup finds one this version supports, it shows an "NPU detected" card with the NPU's name, the chip's name as Windows lists it, and that the AI runs there.
  - **Where the AI runs:** the NPU (recommended) when there is one, the graphics card, or the processor. Picking the GPU or CPU next to an NPU brings up advice against it.
  - **When to scan:** every 6 hours on AC power, or only on Scan now. Asked when the AI runs on the GPU or CPU; on the NPU, scans run hourly.
  - **How hard scans work:** asked on every PC.
    - **In the background:** Windows' efficiency mode, low priority. Slower.
    - **At full speed:** as many cores as it takes, at normal priority.
  - **A copy from GitHub:** when one is installed, a ticked box removes it. The page says why: the Microsoft Store keeps this version up to date by itself.
    - The removal takes the copy's processes, shortcuts, notification name, `heiward:` links, Apps & Features entry and folder.
    - It deletes the registry keys with `reg.exe`. The package's own registry changes stay inside the package: its view of HKCU shows them gone while the user's keys stay.
    - A `Heiward.lnk` is deleted only when it names the GitHub copy's folder: the Store version's desktop shortcut has the same name.
    - Settings, the report and the history stay: the Store version uses them.
    - It runs after the AI components were copied from that folder, and before the Store version's tasks take over the same names.

  The page then runs `hei install --yes` with the answers, shows its output, and switches to the home page once the first scan starts. The GitHub installer asks the same speed question in its console.
- **The review page's port:** both versions use it. When a GitHub copy's page answers there (its ping doesn't say `"store":true`), the Store version stops that copy first, so its own page, and its setup, is the one that opens.
- **The installer:** the package is the install.
  - There's no copy to `%LOCALAPPDATA%\Programs`, no shortcuts, no notification name and no Apps & Features entry: the package has all of those.
  - The AI components are copied from a GitHub copy's folder when there is one, otherwise downloaded. FFmpeg is in the package already.
  - `hei uninstall` stops the scheduled scans and points to **Settings > Apps**.
- **Storage:** the package's folder is read-only.
  - The AI components and the setup marker go in the package's own storage, `%LOCALAPPDATA%\Packages\TheNexus.Heiward_mcanr0hfqkj1g\LocalCache`, which Windows removes with the app.
  - Settings, the report, the history and thumbnails stay in `%LOCALAPPDATA%\Heiward`, on purpose. They're shared with a GitHub copy, so moving to the Store keeps them, and they outlive an uninstall, as they do after `hei uninstall` without `--purge`.
- **Scheduled tasks:**
  - They run the `hei` alias, not the package's `hei.exe`.
  - Uninstalling an MSIX runs none of Heiward's code. So each task first checks through cmd that the alias still exists, and deletes itself when it doesn't.
- **Notifications:** sent under the package's own app ID, `<PackageFamilyName>!Heiward`, which already has the name and logo.

A GitHub copy kept alongside uses the same task names: whichever set up last runs the scheduled scans.

## Not tried yet

**A Store install.** The package has only run registered from a build folder, which is writable and unsigned. From the Store it runs from `WindowsApps`, read-only and signed by the Store. The private-audience submission ([Releasing](#releasing), step 2) is the first time it does.
