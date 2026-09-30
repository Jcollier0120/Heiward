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

## Still to do before the first submission

The package builds and Windows accepts it, but Heiward doesn't run as a packaged app yet: it still installs itself as it does from GitHub. What's left:

- **Knowing it's packaged:** `GetCurrentPackageFullName` returns `APPMODEL_ERROR_NO_PACKAGE` when it isn't.
- **The installer, packaged:**
  - no copy to `%LOCALAPPDATA%\Programs`, no Apps & Features entry, no shortcuts and no `AppUserModelId` key, because the package provides those;
  - `hei uninstall` points to **Settings > Apps**;
  - the GitHub copy, when one is installed too, is offered for removal, so there aren't two Heiwards.
- **Nothing written into the package folder:** it's read-only.
  - FFmpeg is already in the package's `bin\`, so the installer must find it there and not download it again.
  - Check that `CoreUtils.StateFolder` (the AI components) points under `%LOCALAPPDATA%`, not into the package.
- **Scheduled tasks:**
  - They run the alias, not the package's `hei.exe`.
  - Uninstalling an MSIX runs none of Heiward's code, so each task first checks that the alias still exists, and deletes itself when it doesn't.
- **Notifications:** sent as `<PackageFamilyName>!Heiward`, the package's own app ID.
- **First run on the review page:** a Store install has no console, so the setup questions move to the review page, which opens on first run anyway:
  - **Where the AI runs:** the graphics card or the processor. Only asked on a PC without an NPU.
  - **When to scan:** every 6 hours on AC power, or only when asked. Only asked without an NPU.
  - **How hard to work:** every PC gets this question.
    - **Background:** low power and efficient, on the NPU as much as possible, a bit slower.
    - **Full speed:** as many resources as it takes, to finish as fast as possible.
