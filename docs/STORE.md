# Microsoft Store

In the Store, Heiward is an MSIX package that Microsoft signs after certification. People who install it from there get no "Windows protected your PC" prompt, and Smart App Control lets it run. Publishing it costs nothing: individual developer accounts are free.

[`HEI.Agent/store.ps1`](../HEI.Agent/store.ps1) builds the package: `Heiward-<version>.msixbundle` with an x64 and an arm64 package, in `artifacts\store`. Its manifest is [`HEI.Agent/Store/AppxManifest.xml`](../HEI.Agent/Store/AppxManifest.xml).

```
powershell -ExecutionPolicy Bypass -File HEI.Agent\store.ps1
```

## What's in the package

- **`hei.exe`** and the .NET runtime, published as separate files rather than one: the single-file exe from GitHub unpacks its native libraries into `%TEMP%` at startup, where the package's signature doesn't cover them.
- **`Heiward.exe`**, the Start menu entry. It's `hei.exe`'s .NET app host with the Windows GUI subsystem, so it runs `hei open` without a console window.
- **The `hei` command** in a terminal, as an app execution alias. The alias's path, `%LOCALAPPDATA%\Microsoft\WindowsApps\hei.exe`, stays the same across updates while the package's own folder changes with every version. That makes it the path for the scheduled tasks.
- **FFmpeg** in `bin\`, where Heiward looks for it first. The Store signs it with the rest of the package, so Smart App Control lets it load, and nothing is downloaded after install. Its licenses and build notes are in `licenses\FFmpeg`.
  - It's the lean LGPL build from [ffmpeg-winarm64-lean](https://github.com/Jcollier0120/ffmpeg-winarm64-lean), pinned by release and SHA-256.
  - **arm64:** `n8.1.3-2`, the same one the GitHub exe downloads.
  - **x64:** `n8.1.3-3`, the same configuration built for x64.
- **Logos** in `Store\Assets`, rendered from `wwwroot\favicon.svg` by `make-icon.ps1`, and a `resources.pri` that lets Windows pick one per size.

## Partner Center

Heiward is registered under the publisher **The Nexus**, with Store ID `9NX5K0L4CW3D` and package family `TheNexus.Heiward_mcanr0hfqkj1g`. The manifest's identity (**Product management > Product identity**) is filled in.

Still to fill in before the first submission:
1. **The listing:**
   - **Description:** the README's opening.
   - **Screenshots:** from `docs/screenshots`.
   - **Privacy policy URL:** Heiward reads the user's photos and videos, so the Store requires one. A page saying that nothing leaves the PC, and what it downloads, is enough.
2. **The age rating questionnaire.**
3. **Why it needs the restricted `runFullTrust` capability.** For example: "Heiward is a desktop app. It scans the user's drives for duplicate photos and videos, schedules its scans with Task Scheduler, moves the files the user picks to the Recycle Bin, and serves its review page on 127.0.0.1."

## Trying a build locally

With Developer Mode on (**Settings > System > For developers**), register an unpacked package without signing it:

```
Add-AppxPackage -Register artifacts\store\layout-x64\AppxManifest.xml
```

Use `layout-arm64` on an Arm PC. To remove it: `Get-AppxPackage TheNexus.Heiward | Remove-AppxPackage`.

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
    - The removal takes the copy's processes, shortcuts, notification name, Apps & Features entry and folder.
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

## Still to do before the first submission

- **Partner Center:** the listing, the privacy policy and the age rating (above).
- **A real Store install:** a Partner Center flight to a private audience. That's the only way to try the package from `WindowsApps`, signed by the Store; a local `-Register` runs it from a writable folder.
