# Heiward privacy policy

Effective 4 October 2026. It covers Heiward from the Microsoft Store and from [GitHub](https://github.com/Jcollier0120/Heiward/releases).

**In short: nothing about you or your files leaves your PC.** Heiward has no account, no telemetry, no ads and no analytics. The developer receives no data from it. The one exception is yours to turn on: in developer mode, Heiward asks the services that host your code for their open pull requests (see [below](#pull-requests-in-developer-mode)).

## What Heiward reads

To find duplicate photos and videos, and stale developer files, Heiward reads the files on your PC's fixed drives. It leaves out Windows, programs, games, app data and code repositories, and any folder you exclude. It never downloads cloud-only files (OneDrive and the like) to read them.

It reads them on your PC, including the AI matching, which runs on your PC's NPU, graphics card or processor.

In developer mode, so as not to offer a worktree you're still using, it also looks at which folder each of your running programs works in, which programs have a file open that a cleanup couldn't delete (as Windows' Restart Manager tells installers), and, if you use Claude Code, which folders its sessions work in (`%USERPROFILE%\.claude\sessions`) and when their transcripts were last written (`%USERPROFILE%\.claude\projects`; only the date, never what's in them). None of it is kept or sent anywhere.

Once an hour, while its review page is up, Heiward also measures your disks, for you and for the other programs on your PC that ask it (Manor's agents): each fixed drive's size and free space, and how much the big tool caches in your user folder take (Gradle, npm, pnpm, NuGet, pip, the Android SDK, and those of Foundry, GeniEx and Reeve). It only adds up the sizes of the files in those folders: it opens none of them, and keeps no file names. The last reading stays on your PC, and is given to programs on this PC only (`/api/disk`).

With Manor installed, Heiward also looks at whether Reeve and the Steward are installed (whether `%USERPROFILE%\.reeve\app` and `%USERPROFILE%\.steward\app` exist), to leave them their part of a developer's housekeeping.

## What Heiward keeps, and where

Everything it keeps stays on your PC, in `%LOCALAPPDATA%\Heiward`:
- your settings;
- the latest scan's report: the sets of copies it found, with their paths;
- a scan index, so the next scan only reads what changed;
- small thumbnails of the photos and videos on the review page;
- the history of what was cleaned up and kept, if you leave History on in Settings. Turn it off, and no file names are kept for it;
- the last disk reading (`disk.json`): each drive's size and free space, and each tool cache's folder, size and file count.

The Store version also keeps its AI components in its own app storage, which Windows removes with the app. Uninstalling leaves `%LOCALAPPDATA%\Heiward` in place, so a reinstall keeps your choices; delete that folder to remove it too.

Files you choose to clean up go to the Recycle Bin, where you can still restore them.

## The review page

The review page runs in your browser, served by Heiward on your own PC at `127.0.0.1`. It isn't reachable from other devices on your network or from the internet, and it loads nothing from other websites. The same goes for what it tells other programs on your PC, such as the disk reading: other websites open in your browser can't read it.

Without Manor installed, the home page ends with a short card about Manor, with a link to its site. The card is text in the page: nothing is fetched or sent for it, and the site learns nothing unless you open the link, which is then an ordinary visit to a website. **Not now** hides the card, and that choice stays in your settings on your PC.

## What Heiward downloads

When it sets up, Heiward downloads the AI components it needs, and nothing else. Each one is checked against a fixed SHA-256 before use:
- ONNX Runtime and DirectML, from Microsoft (GitHub and NuGet);
- the NPU runtime for your chip: Qualcomm QNN, Intel OpenVINO or Windows ML, from NuGet;
- the image model (DINOv2 small), from GitHub or Hugging Face.

The version from GitHub also downloads FFmpeg, from GitHub; the Store version comes with it.

These downloads are ordinary web requests. Like any, they show your IP address to the site that serves the file (GitHub, NuGet or Hugging Face), under that site's own privacy policy. Heiward sends nothing about you or your files with them.

## Pull requests, in developer mode

Developer mode is off until you turn it on in Settings (or, with Manor installed, in Manor's Developer options, which then decide it). On, the developer page lists your code repositories and, for those hosted on GitHub, Azure DevOps (or Azure DevOps Server and TFS), GitLab, Bitbucket, Gitea or Forgejo, their open pull requests.

To list them, Heiward asks each host while the developer page is open:
- **What it sends:** the names of your repositories hosted there, as their remotes in git name them, and nothing else about you or your files.
- **Signed in as you:** with the host's own command-line tool if you use one (the GitHub CLI, the Azure CLI), with the sign-in git already keeps for that host (Git Credential Manager), or with your Windows sign-in for a server on your own network. A sign-in only ever goes to the host it belongs to, is never written anywhere, and Heiward never asks you for one.
- **What it keeps:** the answer, in memory, for two minutes.

These are ordinary requests to each host, under its own privacy policy. With developer mode off, nothing is asked; nor with Manor and the Steward installed, since the Steward looks after pull requests there.

## Children

Heiward collects no personal information from anyone, children included.

## Changes

Changes to this policy are made here, and the repository's history shows every earlier version.

## Contact

Questions go to [Heiward's issues on GitHub](https://github.com/Jcollier0120/Heiward/issues).
