# Heiward's changelog

Each version of Heiward, newest first. A version's entry is its release's notes: what's new, what changed, and what to do before updating (the Steward's kit, spec/RELEASE-NOTES.md). Versions before 1.7.16 are described in their pull requests.

## 1.9.33

**Releases are checked for a signing connection before they build. This release also brings 1.9.31 and 1.9.32, which weren't released on their own.**

### What changed

- Building a release now checks that the code-signing service is connected before it starts, and if it isn't, says to connect it. Before, the build failed partway through with signtool's "No certificates were found", after trying the same signature three times.

### Before you update

Nothing: it updates itself as usual.

## 1.9.32

**It carries the Steward's kit 2.58.0: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.58.0: Two pieces for agents that talk to git remotes a lot: git's sign-in asked once per host, and the branch heads another agent has just read.

### Before you update

Nothing: it updates itself as usual.

## 1.9.31

**It carries the Steward's kit 2.57.0: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.57.0: An agent of Manor's staff no longer starts from a hidden sign-in task: Castellan, in Windows' Startup apps, starts Manor, and Manor starts its staff.

### Before you update

Nothing: it updates itself as usual.

## 1.9.30

**A fix to Heiward's own tests: nothing changes for you.**

### What changed

- One of Heiward's tests, the one that checks two holders of a graphics card take turns, failed now and then on a busy PC. It read a waiting turn's file while that file was still being written. It now waits until the file is written, as the other programs in line do. Heiward itself is unchanged.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.29

**Heiward starts with Windows the usual way: you'll find it in Startup apps, and it opens in its own window.**

### What's new

- Heiward is listed in Windows' Startup apps (Settings > Apps > Startup, and Task Manager's Startup apps), as Heiward. Turn it off there and it stays off, even after updates. Its page then starts when you open Heiward instead of when you sign in.

### What changed

- When duplicates wait for your review, Heiward's page opens in a window of its own (Microsoft Edge's, with no tabs or address bar), not in your default browser.
- With Castellan, Heiward no longer opens its page when you sign in at all. Castellan's page tells you when copies wait for your review, with a link to Heiward.

### Before you update

- Nothing: it updates itself as usual. The update moves Heiward from its hidden sign-in task to Startup apps.

## 1.9.28

**Deleting or linking duplicates from Heiward's page and desktop app never loses a file the Recycle Bin can't take.**

### What changed

- When you send duplicates to the Recycle Bin from Heiward's page or the desktop app, a file the bin can't take is left where it is, with the reason: one on a network or removable drive, on a drive set to delete files at once, too large for the bin, or one that would push the bin past its size (Windows would then delete what's already in it for good). Before, Windows deleted such files for good without asking.
- On Linux and macOS, a file that can't go to the trash is left where it is and you're told, instead of being deleted for good.
- Replacing a duplicate with a link to the copy you keep makes the link first and only then puts it in the duplicate's place, so a link that can't be made leaves the file as it was.

## 1.9.27

**Uninstalling Heiward looks like setting it up, and asks before it goes.**

### What's new

- Uninstalling Heiward from Settings > Apps opens a window like its setup: the same banner, steps and pictures. It says what goes and what stays, and asks whether to keep your settings and the report, for if you install Heiward again. Uninstall or Cancel, then it shows how it went.

### What changed

- Before, uninstalling from Settings > Apps happened silently, with no window. Tools that uninstall quietly, such as winget, still do, and so does Castellan's uninstall.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.26

**Heiward keeps more of your files out of harm's way when it cleans.**

### What changed

- A worktree is only removed when everything git ignores in it is build output a build makes again. One holding a `.env`, a local database or settings stays, and Heiward says which file kept it.
- What an unfinished worktree removal left behind now goes to the Recycle Bin, except the build output in it, so anything never committed can be put back.
- Build folders (`bin`, `obj`, `build`, `target`) are kept when git tracks files in them or they hold a database.
- Heiward never fills a drive's Recycle Bin past its size: Windows would then delete what's already in it for good. It asks you to empty the bin first. A folder with a path too long for the bin is left for you, too.
- More names count as saved games, and are never touched: folders and files named Save…, Slot… or Profile…, and Steam Cloud's `remote`.
- Heiward's page and pictures speak of Castellan's household staff, and show only the staff that use AI on the NPU.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.25

**Heiward's setup opens in a window of its own, and shows what Heiward does while it sets up.**

### What's new

- Run the downloaded Heiward and its setup opens in a window of its own, in place of a console window: a welcome that
  says what Heiward does, the few questions about your PC, then the setup's steps beside pictures of what Heiward does
  while it gets the AI ready. When it's done, **Open Heiward** turns the same window into Heiward's page.
- The window ends with Castellan, the staff of agents Heiward is part of, and a link to castellan-software.com.
- The Microsoft Store version's first start has the same look.

### What changed

- Started from a script or a command prompt, the setup still asks its questions there.
- If the window is closed before the setup starts, nothing is left running: run the download again to set Heiward up.

### Before you update

Nothing: it updates itself as usual.

## 1.9.24

**The home page says a word about Castellan, the household Heiward belongs to.**

### What's new

- Without Castellan installed, the home page ends with a short card on Castellan: the household of agents that look
  after your PC, Heiward among them, with a link to castellan-software.com. It's text in the page: nothing is fetched
  or sent for it unless you open the link. **Not now** hides it for good, and **The Castellan card** in Settings
  brings it back. With Castellan installed there's no card.

### What changed

- The privacy policy says so, and names Castellan where it said Manor.

### Before you update

- Nothing: it updates itself as usual. Heiward is free, and works the same with or without Castellan.

## 1.9.23

**It carries the Steward's kit 2.56.0: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.56.0: A few agents' rounds at a time, fewer while the PC is busy.

### Before you update

Nothing: it updates itself as usual.

## 1.9.22

**It carries the Steward's kit 2.55.0: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.55.0: The Settings form for a long list: records on one line that says what each does, tabs by name, less said.

### Before you update

Nothing: it updates itself as usual.

## 1.9.21

**It carries the Steward's kit 2.53.0: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.53.0: Demo mode: an agent's page with made-up data, for Manor's pictures of each employee.

### Before you update

Nothing: it updates itself as usual.

## 1.9.20

**It carries the Steward's kit 2.52.0: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.52.0: An update's install no longer holds Manor's update pass for its whole probation.

### Before you update

Nothing: it updates itself as usual.

## 1.9.19

**It carries the Steward's kit 2.51.0: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.51.0: The Steward's safe install for every agent, and sign-in tasks that wait and try again.

### Before you update

Nothing: it updates itself as usual.

## 1.9.18

**A smoother first run, a page that fills a wide screen, lighter scan progress, and a quiet uninstall.**

### What's new

- In a wide window, such as a 2K or 4K screen, the home page uses two columns: the scan, the totals and your drives on the left; the Developer area, the Games, where the duplicates are and the History beside them. Narrower windows keep the single column.
- The Microsoft Store version's setup now also offers **Use more memory to scan faster** and **Keep a history**, the same switches as on the Settings page.

### What changed

- If you choose **Only when I press Scan now** during setup, no scan starts until you press Scan now. Before, the first scan started straight after setup anyway. The same goes for an update when scans run only on demand.
- The tour's first step describes when and how hard Heiward scans as your settings have it, rather than always saying it scans in the background.
- The Game mode and Developer mode switches in the tour now stay as you set them on the first click; before, the first click sprang back.
- While a scan runs, the page asks Heiward only for the scan's progress, every second and a half, and updates just the percent, the progress line and the bars, instead of reloading everything every two seconds. The page stops asking while it's hidden and catches up when you look again. On big libraries, both the page and the PC stay more responsive during a scan.
- Uninstalling from Settings > Apps no longer opens an empty console window. If something stops the uninstall, a message says what and where the details are.
- Links on the page use the theme's link colour, so they're easy to read in both light and dark themes.

### Before you update

Nothing: it updates itself as usual.

## 1.9.17

**Heiward's privacy policy now mentions its daily look for updates.**

### What changed

- The privacy policy says that the version from GitHub asks GitHub once a day for a newer Heiward, as it has done since it began updating itself. The Microsoft Store version leaves updates to the Store.
- Sharper logos for Heiward's Microsoft Store listing.

### Before you update

Nothing: it updates itself as usual.

## 1.9.16

**Heiward's downloads are now code-signed, so Windows names its publisher when you run them.**

### What's new

- Both downloads, for Intel/AMD and for Arm PCs, are signed by Jeremy Collier with a Certum code-signing certificate, and timestamped so the signature stays valid after the certificate expires. When you run one, Windows shows the publisher's name instead of "Unknown publisher".

### What changed

- Windows may still say "Windows protected your PC" right after a release, until enough people have run it. If it does, click **More info**, check the publisher is Jeremy Collier, then **Run anyway**.
- The Microsoft Store version is unchanged: Microsoft signs it, as before.

### Before you update

Nothing: it updates itself as usual.

## 1.9.15

**It carries the Steward's kit 2.49.0: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.49.0: Developer Pro: the Wright's and the Bailiff's work with the rest of the staff, shown only where it's sold.

### Before you update

Nothing: it updates itself as usual.

## 1.9.14

**It carries the Steward's kit 2.48.0: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.48.0: About, beside Settings: what's read once, out of the way of what's used every day.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.13

**An About button beside Settings, as on Manor's page.**

### What changed

- About, beside Settings in the title bar, has what you read once: which version this is and where it came from, its license, and where its work runs. They were at the foot of Settings.
- Settings keeps every switch: Update by itself and the "In a manor" card are now under Updates.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.12

**It carries the Steward's kit 2.47.0: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.47.0: A long page, and long Settings, in tabs; a short one stays a single column.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.11

**The Store version of Heiward is built for every release, and sent to the Store once that is set up.**

### What changed

- Each release of Heiward now also builds its Microsoft Store package. Once the owner has connected Partner Center, each one is submitted to the Store by itself, and Store installs update through Windows as before.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.10

**Heiward installed from GitHub keeps itself up to date.**

### What's new

- Once a day Heiward looks for a newer release. When there is one, it downloads it, checks it against the release's checksums and installs it, keeping your settings: where the AI runs, the schedule and the graphics card. It never updates during a scan.
- The About card has an **Update by itself** switch to turn this off, and says when Heiward last looked and what it found.
- `hei update` installs the newest release now (`hei update --check` only looks).

### What changed

- A copy that something else keeps up to date is left alone: the Store version, a Heiward that Manor employs, and development builds.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.9

**It carries the Steward's kit 2.46.0: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.46.0: Every agent's Settings can be in tabs.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.8

**While Heiward scans, its page says plainly what it's doing and how far it has got.**

### What changed

- A scan under way now has its own panel at the top of the page: what Heiward is doing right now, how far through it is, and the scan's three steps (find photos and videos, check new and changed files, compare and make the report) with the ones done ticked.
- Once every drive's files are checked, the drive cards say so, instead of each showing the same comparing bar.
- The line at the top right says Scanning now while a scan runs.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.7

**It carries the Steward's kit 2.45.0: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.45.0: Each agent Castellan sells checks the trial's end itself, from the license Manor holds.

### Before you update

- Nothing: it updates itself as usual. Manor's own copy of the keys and the license check (its license-keys.ts and license.ts) can move to this kit's license.ts in a later Manor change.

## 1.9.6

**It carries the Steward's kit 2.44.0: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.44.0: A release's notes bring every version merged since the release before; entries can wait in changes/.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.5

**It carries the Steward's kit 2.43.5: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.43.5: A queue ticket can say what the request is for, and an agent can say it is setting up.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.4

**It carries the Steward's kit 2.43.4: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.43.4: The kit's MIT license comes with the kit, as `src/kit/LICENSE`.

### Before you update

Nothing: it updates itself as usual.

## 1.9.3

**Heiward's first scan waits its turn among the manor's newcomers, then runs at full speed.**

### What's new

- **Settling into the manor.** In a manor, Heiward's first scan, which reads every drive, no longer runs at the same time as the other agents' heavy first work. When everything is installed together, each agent does its first round in turn, so your PC isn't swamped. Heiward waits for the one before it, then scans at full speed while it has its turn, since nothing else heavy is running then. A game still makes it step aside. Every scan after that runs as you chose.
- While it waits, or while its first scan runs, the review page says so, with a small drawing of the manor's door in your theme's colours.
- Scan now never waits. Without a manor on this PC, or with Manor set to run everything at full speed, Heiward scans as before.

### Before you update

- Nothing: it updates itself as usual. A PC where Heiward has already scanned is never held up.

## 1.9.2

**It carries the Steward's kit 2.41.0: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.41.0: A reranker the keeper sets up and keeps: a fourth serve kind, `rerank`.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.1

**It carries the Steward's kit 2.40.0: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.40.0: Settings obey the Developer options switch from the schema alone, and the kit's own parts leak nothing more with it off.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.0

**Heiward lends Chamberlain its eye for pictures, so duplicate documents can be found.**

### What's new

- Chamberlain, finding documents that are copies of each other, can now ask Heiward whether two documents hold the same pictures. Two documents are copies only when they have the same words and the same pictures: when a picture was added, removed, swapped, cropped or retouched, they are different versions and are left as they are. Heiward compares the pictures by look, with the same rules it uses for your photos, so a picture that was only saved again or made smaller still counts as the same.
- It does this only when the PC can spare it: never while you play a game or use a full-screen program, or while Heiward is paused. During a scan, or without AI matching installed, it compares the pictures pixel by pixel alone, which gives the same answer. It reads no files for it and keeps nothing.
- Nothing changes on Heiward's own page.

### Before you update

- Nothing: it updates itself as usual.

## 1.8.0

**Game mode: Heiward looks after gamers' very large files, and its page speaks plainly to everyone who isn't a developer.**

### What's new

- **Game mode**, a new switch in Settings, off until you turn it on. Once a day Heiward looks at your games and lists them in a new **Games** area on the home page, by game, by drive and by kind, each with its size, what it is, and what removing it means:
  - your installed games from Steam, Epic Games, GOG GALAXY, the EA app, Ubisoft Connect, Battle.net and Xbox, with when you last played them where the launcher records it (Steam does);
  - leftovers of games you've uninstalled, and workshop downloads of games no longer installed;
  - the launchers' download caches;
  - shader caches: those of uninstalled games are ticked for you; those of games you have never are, as their first launch afterwards may stutter;
  - crash dumps and crash reports of your games;
  - the same game installed twice, and games you haven't played in months, biggest first, with how to move or uninstall each with its own launcher. Heiward doesn't move or uninstall games itself.
- Everything you remove there goes to the Recycle Bin. An installed game's own files and your saved games are never touched, nor anything of a launcher or a game while it's running.
- Automatic cleanup can take what games leave behind once you turn it on: crash dumps, download caches, and leftovers and shader caches of uninstalled games, to the Recycle Bin.
- At a glance on the home page has a **Games** slice.

### What changed

- With developer mode off, the page no longer shows technical details: no commands, settings-file or log names, model or runtime names, or the commit a build came from. The title bar's AI badge and Settings say the same things in plain words.
- With Manor installed and its Developer options off, Settings no longer shows the Developer mode card.

### Before you update

- Nothing: it updates itself as usual. Game mode stays off until you turn it on in Settings.

## 1.7.47

**It carries the Steward's kit 2.39.0: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.39.0: Every agent gets Manor's Developer options switch, and the kit's own parts of every page obey it.

### Before you update

- Nothing: it updates itself as usual. On a PC where Manor's Developer options are off, or with no Manor, each agent's Settings page stops showing the settings file's path and the technical lines in Where its work runs. Turn Developer options on in Manor to see them again.

## 1.7.46

**It carries the Steward's kit 2.36.1: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.36.1: GenieX 0.8.0 is the NPU's server on a Snapdragon.

### Before you update

- Nothing: it updates itself as usual. This kit comes after 2.36.0; 2.33.0 (Steward 0.19.4) is still to be released and doesn't depend on it.

## 1.7.45

**It carries the Steward's kit 2.35.0: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.35.0: A release of an agent Castellan sells goes to the Exchequer, and no longer to the public releases repository.

### Before you update

- Nothing: it updates itself as usual. Agents for sale stop appearing in the public releases repository once the Exchequer 0.6.0 is deployed with its migration applied. Until then, releases go there as before.

## 1.7.44

**It carries the Steward's kit 2.34.0: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.34.0: An agent's rounds never stop silently.

### Before you update

- Nothing: it updates itself as usual. This kit comes after 2.33.0 (Steward 0.19.4); if 2.33.0 isn't released yet, release it first.

## 1.7.43

**It carries the Steward's kit 2.32.1: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.32.1: The kit names the app the person bought: Castellan.

### Before you update

- Nothing: it updates itself as usual.

## 1.7.42

**It carries the Steward's kit 2.32.0: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.32.0: A release goes to the agent's own repository, and to a releases repository only where one is named.

### Before you update

Nothing: it updates itself as usual.

## 1.7.41

**It carries the Steward's kit 2.31.0: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.31.0: Local AI on every Copilot+ NPU, installed by setup: Qualcomm Snapdragon, Intel Core Ultra and AMD Ryzen AI.

### Before you update

- The accelerators move from Reeve's `config.json` to `%USERPROFILE%\.manor\accelerators\config.json` the first time the Smith (or Reeve's keeper) reads them after updating: a copy, word for word, and Reeve's file is left as it was. Nothing to do.
- On a PC whose NPU isn't set up yet, the Smith's or Manor's Set up now offers its maker's server and models (several GB) and asks first.

## 1.7.40

**It carries the Steward's kit 2.29.0: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.29.0: Every agent's page fits a phone: the title bar's action and wide tables no longer push the page sideways.

### Before you update

Nothing: it updates itself as usual.

## 1.7.39

**It carries the Steward's kit 2.28.1: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.28.1: Security fix: a value with curly quotes in it could break out of a PowerShell string.

### Before you update

Nothing: it updates itself as usual.

## 1.7.38

**A security fix: a folder name or notification text with curly quotes in it can no longer break out of Heiward's PowerShell commands.**

### What changed

- Windows PowerShell treats the curly quotes ‘ ’ ‚ ‛ as quote marks. Making Heiward's shortcut when it installs, and showing a Windows notification from the app, now escape every one of them, so a profile like `C:\Users\O’Brien` installs as it should and stays a name, never a command.

### Before you update

Nothing: it updates itself as usual.

## 1.7.37

**Heiward's own checks of developer mode no longer depend on what else is installed on the PC.**

### What changed

- The tests behind developer mode's worktree cleaning now run the same on a PC with Manor and Reeve installed as anywhere else, and the ones that watch a program working in a folder wait for it to have started. Nothing changes in what Heiward does.

### Before you update

Nothing: it updates itself as usual.

## 1.7.36

**The "In a manor" card is put away for now: it comes back when the manor is ready to join.**

### What changed

- The home page no longer ends with the "In a manor" card, and Settings no longer has its switch. Nothing else on the page changes, and Heiward works just as before.
- If you chose **Not now**, that choice is kept for when the card returns.

### Before you update

Nothing: it updates itself as usual.

## 1.7.35

**A short card at the foot of the home page about Manor, the household Heiward can join.**

### What's new

- Without Manor on the PC, the home page ends with an "In a manor" card: a few lines on the agents Heiward can work beside, all private and all on this PC, and a link to see the manor. It sits below everything else, never above your drives, and it's text in the page: nothing is fetched or sent for it.
- **Not now** hides it for good. To bring it back, turn on **The "In a manor" card** under About Heiward in Settings.
- With Manor installed there's no card: the title bar's **Back to** link already says where Heiward works.
- Heiward stays free and complete on its own: nothing in it depends on Manor.

### Before you update

Nothing: it updates itself as usual.

## 1.7.34

**It carries the Steward's kit 2.28.0: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.28.0: Required settings: an agent that can't work without something from you waits for it, and onboarding asks for it.

### Before you update

Nothing: it updates itself as usual.

## 1.7.33

**A short tour of the review page the first time you open it.**

### What's new

- The first time you open Heiward's page after installing it, a tour walks you through it in three steps: what Heiward does, the choices that are yours (Developer mode, what it scans, automatic cleanup), and the page itself, part by part, each outlined as it goes. Skip it any time with Skip or Esc; take it again at `#/tour` on the page.
- It's Heiward's own, so it works the same with or without Manor. When Manor hires Heiward, its **Take the tour** opens it too, with a way back to Manor at the end.

### Before you update

Nothing: it updates itself as usual.

## 1.7.32

**It carries the Steward's kit 2.27.0: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.27.0: An accelerator's name is what the PC calls it, never a setting.

### Before you update

Nothing: it updates itself as usual.

## 1.7.31

**It carries the Steward's kit 2.26.0: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.26.0: The kit names no one's GitHub account, private repository or folder.

### Before you update

Nothing: it updates itself as usual.

## 1.7.30

**It carries the Steward's kit 2.24.0: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.24.0: What every agent asked Manor, its owner or GitHub for by hand, from the kit.

### Before you update

Nothing: it updates itself as usual.

## 1.7.29

**It carries the Steward's kit 2.23.0: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.23.0: Settings in React, and short; and a new hire's tour of its page.

### Before you update

Nothing: it updates itself as usual.

## 1.7.28

**It carries the Steward's kit 2.22.0: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.22.0: The react part's components are GamerNexus's UI kit, in the manor's look.

### Before you update

Nothing: it updates itself as usual.

## 1.7.27

**It carries the Steward's kit 2.21.0: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.21.0: A page drawn in the browser, with React: the kit's react part.

### Before you update

Nothing: it updates itself as usual.

## 1.7.26

**It carries the Steward's kit 2.19.0: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.19.0: Every release says what it brings: its notes are the agent's CHANGELOG.md entry for the version.

### Before you update

Nothing: it updates itself as usual.

## 1.7.25

**It carries the Steward's kit 2.18.0: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.18.0: Non-employee projects: repositories that ride along with the manor.
- A release's notes now come from its entry in this changelog, after a first line naming the version, the commit it was built from and the kit it carries.

### Before you update

Nothing: it updates itself as usual.

## 1.7.24

**It carries the Steward's kit 2.17.0: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.17.0: A release is built, never the readable source, and published in the public Jcollier0120/Manor-releases.

### Before you update

Nothing: it updates itself as usual.

## 1.7.23

**It carries the Steward's kit 2.16.0: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.16.0: Every agent's page uses the width of the window.

### Before you update

Nothing: it updates itself as usual.

## 1.7.22

**It carries the Steward's kit 2.15.0: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.15.0: Offline is waited out, never a failure.

### Before you update

Nothing: it updates itself as usual.

## 1.7.21

**It carries the Steward's kit 2.14.0: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.14.0: The title bar stays at the top as the page scrolls.

### Before you update

Nothing: it updates itself as usual.

## 1.7.20

**It carries the Steward's kit 2.13.0: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.13.0: The NPU first.

### Before you update

Nothing: it updates itself as usual.

## 1.7.19

**It carries the Steward's kit 2.12.1: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.12.1: A release zip's name has no spaces.

### Before you update

Nothing: it updates itself as usual.

## 1.7.18

**It carries the Steward's kit 2.12.0: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.12.0: A release announces its agent to every Manor, when the checkout has manor-agent.json.

### Before you update

Nothing: it updates itself as usual.

## 1.7.17

**It carries the Steward's kit 2.11.0: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.11.0: Keeping the model servers moves into the kit, for the Smith.

### Before you update

Nothing: it updates itself as usual.

## 1.7.16

**It carries the Steward's kit 2.10.0: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.10.0: Manor can keep the graphics card out of model work on a PC with an NPU.

### Before you update

Nothing: it updates itself as usual.
