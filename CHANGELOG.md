# Heiward's changelog

Each version of Heiward, newest first. A version's entry is its release's notes: what's new, what changed, and what to do before updating (the Steward's kit, spec/RELEASE-NOTES.md). Versions before 1.7.16 are described in their pull requests.

## 1.9.3

**Heiward's first scan waits its turn among the manor's newcomers, then runs at full speed.**

### What's new

- **Settling into the manor.** In a manor, Heiward's first scan, which reads every drive, no longer runs at the same time as the other agents' heavy first work. When everything is installed together, each agent does its first round in turn, so your PC isn't swamped. Heiward waits for the one before it, then scans at full speed while it has its turn, since nothing else heavy is running then. A game still makes it step aside. Every scan after that runs as you chose.
- While it waits, or while its first scan runs, the review page says so, with a small drawing of the manor's door in your theme's colours.
- Scan now never waits. Without a manor on this PC, or with Manor set to run everything at full speed, Heiward scans as before.

### Before you update

- Nothing: it updates itself as usual. A PC where Heiward has already scanned is never held up.

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
