# Heiward's changelog

Each version of Heiward, newest first. A version's entry is its release's notes: what's new, what changed, and what to do before updating (the Steward's kit, spec/RELEASE-NOTES.md). Versions before 1.7.16 are described in their pull requests.

## 1.9.90

**The developer check leaves a drive scanned only when you ask alone, and finds the caches you moved to a Dev Drive.**

### What's new

- On a drive set to Scan only when I ask, the developer check reads nothing: projects, worktrees, package caches, emulators, temp files and crash dumps there aren't looked for, measured or asked of git, even when you reach them through a junction or a folder the drive is mounted in. What the last check found there stays listed as it was, with a line saying which drive it's on and when it was measured.
- Scan this drive now, on such a drive's page, checks its developer items too, so they're up to date.
- Package caches are found where their tools' settings put them, as Windows' Dev Drive guidance moves them: npm's cache in .npmrc, pnpm's store-dir, NUGET_PACKAGES and NuGet's other caches, PIP_CACHE_DIR, GRADLE_USER_HOME, CARGO_HOME, GOCACHE, YARN_CACHE_FOLDER and Maven's maven.repo.local. The folder a tool used before is listed too, ticked: nothing uses it any more.

### What changed

- Automatic cleanup leaves developer items and merged branches on a drive scanned only when you ask for you, as it does duplicate sets there ("It's on a drive scanned only when you ask, so it waits for you"). Clean still works on them when you ask, checking each again first.
- A worktree on such a drive keeps its branch counted as checked out, so pruning merged branches never takes it.

### Before you update

- Nothing: it updates itself as usual. If you want the developer check to keep cleaning up a drive you set to Scan only when I ask (a Dev Drive holding your temp files, say), right-click that drive under This PC and choose Scan automatically again.

## 1.9.89

**It carries the Steward's kit 2.136.1: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.136.1: Urgent fix: the PowerShell worker starts in a signed release. Since kit 2.123.0 signed ps-worker.ps1, it was too long for a command line: no agent released since could start its worker (spawn ENAMETOOLONG), and every `powershell()` call failed.

### Before you update

- Nothing in an agent's code: take the kit and release. An agent released on kit 2.123.0 or later needs this kit for its PowerShell to work.

## 1.9.88

**It carries the Steward's kit 2.135.4: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.135.4: A 16 GB Snapdragon X2 reranks on its NPU, beside chat and embeddings, as a bigger PC does. Measured there with a 16 GB PC's memory: nothing failed, chat wasn't slowed, and the NPU held 4.7 GB against its 6.2 GB share.

### Before you update

- Nothing: it updates itself as usual. Reeve's sharper search, set up again on a 16 GB X2, moves its reranker to the NPU.

## 1.9.87

**It carries the Steward's kit 2.135.0: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.135.0: The text reader on the processor runs in Windows' efficiency mode at a gentle pace: the same lines read, for about half the energy a picture.

### Before you update

- Nothing: it updates itself as usual. The signed helpers are unchanged, so nothing is compiled anew.

## 1.9.85

**It carries the Steward's kit 2.114.0: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.114.0: The text reader reads with our own model on the NPU: on a Snapdragon it reads 1,211 of the check's 1,253 lines exactly, where Windows OCR read 733.

### Before you update

- Nothing: take the kit as usual. On a Snapdragon, the first read after an agent takes it starts the 226 MB fetch in the background. Windows OCR reads until the fetch is done, and the first start then compiles the NPU's graphs (about 17 s). Every agent on the PC shares the one folder, so it's fetched once.

## 1.9.84

**It carries the Steward's kit 2.113.2: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.113.2: Intel's pictures stay on the NPU, and the guide says why.

### Before you update

- Nothing: take the kit as usual. Only the guides change.

## 1.9.83

**It carries the Steward's kit 2.111.0: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.111.0: AMD's NPU server is FastFlowLM 1.0.10. A generation can say which device each work spends least on, and on AMD's Ryzen AI 300 pictures go to the Radeon.

### Before you update

- Nothing for most agents: take the kit as usual. Every new field is optional, so an older kit (the Smith pins 2.99.1) reads the catalog and config.json as before. A `candidates` caller without `workOrder` gets the order it got before. A program in the NPU's line that doesn't write a ticket's `work` (npu-embed, Heiward's) is read as before; its requests count as no pictures.
- A staff member that sends several pictures at once may say how many (`AskOptions.batch`), so a folder's worth starts the Radeon's server on AMD at the first picture.
- An AMD PC gets 1.0.10, and its order, the next time setup runs: the keeper's look finds 1.0.10 missing and leaves it for setup. A PC on any other NPU gets no `workOrder`.
- Reeve's own routing (`src/routing.ts`) doesn't read `workOrder` yet. On AMD his embeddings go to the Radeon first by the auto order anyway.

## 1.9.81

**It carries the Steward's kit 2.106.0: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.106.0: A generation of an NPU can say the biggest model size a manager has tested on it; the Smith offers no bigger.

### Before you update

Nothing: it updates itself as usual.

## 1.9.77

**It carries the Steward's kit 2.99.1: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.99.1: No model server is started while the manor is stopped from Manor's Settings.

### Before you update

- Nothing for an agent: take the kit as usual. It needs a Manor with **Stop the manor**; with an older Manor nothing changes.

## 1.9.76

**Claude Code's old temp files go one session at a time, and never those of a session still in use.**

### What's new

- Developer mode's **Temp files and crash dumps** has a new row, **Claude Code sessions' temp files**: the folder Claude Code keeps in your Temp folder for each session (its scratchpad, its tasks' output), counted and measured a session at a time. A session's folder is ticked once nothing in it has changed for 7 days (`tempOlderThanDays`), and automatic cleanup takes it with your other temp files.
- Never one a session may still use, whatever its age: a session that's running, one you can still resume (it was used in those 7 days), one the Claude app lists as open rather than archived, or a folder a program has a file open in. Each is asked again just before anything is deleted.

### What changed

- Claude Code's temp folder no longer counts as a single Temp entry. Live sessions write in it every minute, so as a whole it was never old enough to go, and it could grow to tens of gigabytes. The rest of what's in it is judged as your other temp files are, and a project's folder goes once its sessions' have.

### Before you update

- Nothing: it updates itself as usual. The row appears after the next developer check.

## 1.9.75

**It carries the Steward's kit 2.96.2: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.96.2: An update's copy of the version before goes once the new one has held up, a swap cut short is put back, and uninstall takes the kept copies with it.

### Before you update

- Nothing: take the kit and release as usual. An update runs the new release's own kit, so an agent behaves this way from the update after the one that brings it this kit.

## 1.9.74

**It carries the Steward's kit 2.96.1: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.96.1: A strict page sends a browser that isn't signed in to Castellan, which opens that same page signed in.

### Before you update

- Nothing: pages aren't strict unless `KIT_STRICT_PAGES=1`, and an agent with a guard takes the let-go steps into its own install-guard.ps1.

## 1.9.73

**It carries the Steward's kit 2.95.0: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.95.0: Ready for Castellan's "Everyone on this PC" install: each Windows account's tasks in a Task Scheduler folder of its own, and the model files once per PC.

### Before you update

- An agent that names its task in its own code (`\<Name>\Home page`, or `schtasks /Run` by hand) should use `TASK_NAME`, or `runOwnTask([TASK_NAME, OLD_TASK_NAME], taskDeps())`. One that only shows `TASK_NAME` in its usage text needs nothing.
- Nothing else: the old tasks move at each agent's next update, with nothing for the person to do.

## 1.9.72

**Each Windows account on a PC has its own Heiward tasks, so two accounts never share or fight over one.**

### What changed

- Heiward's scheduled scan and its other tasks are now in a Task Scheduler folder of each account's own, named after the account: `\Heiward (<account>)\Scan`. Before, every account's were in one `Heiward` folder, where the names belonged to whichever account installed Heiward first. This is how Castellan names each of its programs' tasks when it's installed for everyone on a PC.
- When Heiward updates, it moves your account's tasks from their old names to the new folder. It moves only your own: another account's Heiward keeps its tasks as they are. The Microsoft Store version moves them the first time its page starts after the update, and your scans run as scheduled in between.
- Uninstalling removes your account's tasks under both the new names and the old ones, and never another account's.

### Before you update

- Nothing: it updates itself as usual.

**It carries the Steward's kit 2.94.0: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.94.0: The guard's administrator prompt names Castellan, not Windows PowerShell, where Manor can show it.

### Before you update

- Nothing in an agent's code: the Watchman and the Lamplighter have this in their own guard already, and no agent takes it from the kit yet. An agent that turns its guard on or off through `guardCommand` or `runElevated` gets the Castellan prompt once its Manor is 0.17.7 or later, with no change of its own.

## 1.9.70

**It carries the Steward's kit 2.93.3: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.93.3: A test file that never ends no longer holds a whole check run, and the PC's turn, until its time limit.

### Before you update

- Nothing in an agent's code. A test file that took longer than ten minutes at the pace now fails: none of the manor's comes near (their whole suites take a few). A file that keeps its process alive after its tests is still a bug to fix in the agent (stop what it started, even when a test fails before its `finally`), but it no longer stalls the run.

## 1.9.69

**Urgent fix: Heiward installs on a PC where another account installed it first, and its FFmpeg runs where Smart App Control is on.**

### What changed

- On a PC where another Windows account had installed Heiward first, the install stopped at "Task Scheduler refused 'Heiward\Scan': Access is denied", and Heiward had no shortcut. Task Scheduler's names are the PC's, not each account's, so the other account's scan task held the name. Heiward now puts this account's tasks in a folder of their own inside Heiward's, named after your account, and finds them there from then on.
- Heiward reads your videos with FFmpeg, which it downloads when it installs. The builds it downloaded weren't signed, and Smart App Control blocks a program no one signed that it doesn't know: where it's on, videos couldn't be scanned, with a Smart App Control notice for each try. Heiward now downloads the manor's FFmpeg, whose every program and library is signed, on both x64 and ARM64 PCs.
- Where Smart App Control is on, an unsigned FFmpeg Heiward downloaded before is replaced with the signed one when Heiward updates.

### Before you update

Nothing: it updates itself as usual.

## 1.9.68

**The Developer area no longer lists repositories that tests left in your Temp folder.**

### What changed

- The Developer area leaves out repositories inside a Temp folder: your account's Temp, and a Temp or Tmp folder at the root of a drive. Tests and tools make throwaway repositories there by the thousand and leave them behind; they buried your own projects in the list. Their space is still offered for cleanup under Temporary files once they're old enough.

### Before you update

Nothing: it updates itself as usual.

## 1.9.67

**It carries the Steward's kit 2.91.0: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.91.0: The kit's own helper programs are signed, and a start that Smart App Control blocked gets one more try.

### Before you update

- Nothing in an agent's code. The prebuilt helpers come with the kit's node part, about 70 KB in `src\kit\prebuilt\`.
- A helper already compiled on a PC stays in its folder, unused once the signed one is there.

## 1.9.65

**The tour holds the page until you answer it or skip it.**

### What changed

- While Heiward's tour shows, the page under it is dimmed and can't be clicked, as Castellan's own welcome does: each step is answered, or the tour skipped, before you go on. Walking the page's parts, the card sits in the corner and the page is only lightly dimmed, so the part it names stays easy to see. Escape still skips it.

### Before you update

Nothing: it updates itself as usual.

## 1.9.64

**A new install waits for you before its first scan.**

### What's new

- The tour's last step asks when the first scan should run: **Scan now**, **Scan by itself** (it runs with the next scheduled scan, at the pace you chose), or **Later**.
- Until you choose, a banner on Heiward's page asks the same. At a manor, Manor's page lists "Heiward's first scan waits for you".

### What changed

- Installing Heiward no longer starts a first scan, at full speed, the moment setup finishes. It scans nothing until you have opened its page, seen the tour, and pressed Scan now or chosen Scan by itself. Scheduled scans skip themselves until then.
- The Store version's setup button now reads **Set up**: the first scan waits for your choice there too.

### Before you update

Nothing: it updates itself as usual. An update scans as before: only a new install waits.

## 1.9.63

**It carries the Steward's kit 2.88.2: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.88.2: The helper that keeps the PC awake for the staff's work no longer opens a terminal. Before, the wake task before each of the Steward's builds put a terminal window on the desktop for up to 65 minutes. Where kit 2.88.1 wasn't released before it, this release brings it too.

### Before you update

- Nothing in an agent's code. Each agent builds the new helper (a few seconds, once per PC) the first time it holds the PC awake, and the Steward re-registers its wake task. A terminal the old helper already opened closes when its hold ends, or you can close it: closing it ends nothing but that hold.
- The old `awake-*.exe` stays in `%USERPROFILE%\.npu-agent\kit` (a few KB). Nothing uses it once the wake task has moved.

## 1.9.62

**It carries the Steward's kit 2.88.0: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.88.0: FFmpeg and making a video smaller are now in the kit: finding FFmpeg, fetching the manor's build once for every agent, choosing the video encoder, and re-encoding one video with its checks. The Miller and the Chronicler each carried their own copy of this, pinned to different builds. Nothing changes for either until it takes the kit's up.

### Before you update

Nothing: it updates itself as usual.

## 1.9.61

**A drive written in settings.json by its letter alone, such as "D:", now means the whole drive.**

### What changed

- A folder written in settings.json as a drive letter alone, such as `"folders": ["D:"]`, is now scanned as that whole drive, the same as `"D:\\"`. Windows reads "D:" as the folder Heiward's process is working in on that drive, which on the drive it was started from is rarely the root. A scan could then look at one folder of the drive instead of all of it. Heiward's own page always saves a drive with its backslash, so only a hand-edited settings.json was affected.
- The same applies wherever Heiward checks which drive a folder is really on, for a drive mounted in a folder or a junction. No part of Heiward asks that about a bare drive letter today, but a test that did failed on a PC where C:\Projects is a drive mounted in a folder.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.60

**Manor's disk tile asks Heiward for the drives at a glance alone, not for everything the review page shows.**

### What changed

- Heiward's page has a new `GET /api/glance`, which answers with the home page's "At a glance" alone: the drives' size and free space, the used space by kind, and what could be reclaimed. Manor's disk tile asks for it once a minute. It used to ask for `/api/state`, everything the review page draws: the review lists, the History, the settings, the graphics cards and the schedule. On this PC that was 77 KB a minute, of which Manor kept 1 KB. The glance reads only the report, the index, your decisions, and the developer and games reports. It takes about half the processor time per request that `/api/state` does: 7 million cycles against 13 million, measured over 300 requests in a row.
- The Recycle Bin's size is read at most every 5 minutes, and again as soon as Heiward moves files there. It used to be read once a minute, so Manor's tile, asking once a minute too, read it every time.
- The graphics cards are looked up at most every 5 minutes instead of every minute, for the review page's settings.

### Before you update

- Nothing: it updates itself as usual. Manor uses the glance from its next version on, and asks an older Heiward for `/api/state` as before.

## 1.9.59

**Scans leave package caches out, list a folder that was added, moved or deleted by itself instead of walking the whole drive, and keep up with a busy drive's journal. The review page uses less of the processor while it waits.**

### What changed

- Package caches moved out of their usual places are left out of scans, shown as "Package cache" on the review page. By default npm's, NuGet's, pip's, Cargo's and other package managers' caches are dot-folders or live in AppData, and scans already leave those out. A Dev Drive set up by Microsoft's guide moves them to plain folders (D:\packages\nuget and the like), named by variables such as NUGET_PACKAGES and npm_config_cache. Scans walked those folders and listed the packages' icons as duplicates of each other: on one PC, 76 of the report's 94 paths. Heiward now reads those variables (and Maven's -Dmaven.repo.local in MAVEN_OPTS) and leaves the folders out. It ignores a variable that names your profile or one of your own folders.
- A folder added, moved in or made visible where scans look is now listed by itself, with what's below it. Before, one new folder anywhere a scan looks (a clone, an unpacked download, a build's output) made the next scan walk the whole drive, 58 to 96 s on C:\. A folder that is deleted, moved away, or made a code repository, hidden or a link now drops its photos and videos from the listing. A folder created empty changes nothing, so a scheduled scan still doesn't run.
- The review page now checks each drive's change journal every ten minutes. Once a quarter of the journal holds changes the next scan hasn't read, the page reads them and keeps what changed where scans look, and where it read up to. The journal on C:\ holds 32 MB, about 80 minutes of a busy afternoon. Scans an hour apart found it overwritten five times on one day, and each of those scans walked all of C:\. A scan now reads only what came after the page's last look. A look at a full journal takes about a second at low priority, and a quiet drive's journal isn't read at all. The page writes nothing while a scan runs, and never over a listing a scan saved since it looked.
- Placing an hour's changes takes half the time it did: most of the folders they happened in are a build's or an installer's, made and deleted within the hour, and Heiward no longer asks Windows where a folder the journal saw deleted is.
- A folder deleted elsewhere on the drive while a scan placed its changes no longer makes that scan walk the whole drive.
- The log now tells an overwritten journal from one Windows wouldn't read ("the journal couldn't be read (Windows error 87)"). Before, both read "the journal no longer reaches back to the last scan".
- A walk lists each folder once, and gets its photos and videos, its subfolders, and whether it's a code repository from that one listing. It used to list each folder twice and ask every subfolder about a dozen repository markers, one at a time: 84,728 lookups for the 7,057 folders of C:\Projects. Walking C:\Projects with its package caches went from a median of 1.8 to 2.0 s down to 0.2 to 0.4 s.
- The review page's process uses less of the processor while it waits. It took about 3.4 s an hour while idle, three-quarters of that measuring the tool caches' sizes every hour (71,000 files in 13,400 folders). It now measures them once a day, and gives each drive's free space as Windows reports it when a program asks.
- The review page's ping now says whether Heiward is on duty, since when it has been paused, and its one-line status, as `hei status --json` does. Manor used to run `hei status` every two minutes to learn them, starting Heiward and Task Scheduler each time: about 20 to 30 s of processor time an hour. With them in the ping, Manor doesn't need to.
- The page now asks Task Scheduler for the next scan every half hour, or once that scan's time has come, instead of every minute. It counts the sets waiting for review again only when the report, your decisions, automatic cleanup or your settings change, or every half hour.

### Before you update

- Nothing: it updates itself as usual. The first scan after the update walks each drive once, because Heiward lists differently and the new exclusions are part of the listing's key. Later scans go by the drive's journal again.

## 1.9.58

**The badge says "Running on the NPU" only while a scan is using the NPU.**

### What changed

- The badge at the top of the page read "Running on the NPU" whenever the last scan had used the NPU: through every minute of a long scan, and after it ended. A scan uses the NPU only while it matches pictures and video frames. It spends most of its time listing, reading and comparing files, and making the report, and the NPU rests then. The badge now says "Running on the NPU" only within seconds of the NPU working for the scan, as Manor's Accelerators card does, and "NPU ready" the rest of the time.
- The badge's tip says which it is: the NPU matching pictures for this scan now, set up for this scan and used only while it matches pictures, or the NPU that ran the last scan.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.57

**It carries the Steward's kit 2.84.0: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.84.0: An agent can now tell when a vendor's screen saver keeps putting the PC to sleep, and say which setting stops it. spec/ROUND.md no longer takes Windows' SleepButton / SC_MONITORPOWER for a sleep the person chose.

### Before you update

- Nothing: it updates itself as usual. Showing the line is up to each agent; nothing calls `saverSleeps()` by itself.

## 1.9.56

**It carries the Steward's kit 2.83.2: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.83.2: A file an agent only reads is now parsed once while it is unchanged, not on every read. The Steward reads Manor's staff list (64 KB) and its own settings every two seconds, and now parses them only when they change.

### Before you update

- Nothing: it updates itself as usual. If your agent calls `readJsonShared`, don't change what it returns: it is frozen.

## 1.9.55

**It carries the Steward's kit 2.83.0: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.83.0: Graphics that share the PC's memory, and the processor, now chat and see with one Qwen3-VL-2B server in place of Qwen3-4B and Qwen3-VL-4B: about twice as fast, about 2 GB less memory than Qwen3-VL-4B alone, and the same scores. Cards with memory of their own keep the 4B models. On the Adreno, setup takes llama.cpp b11715 or later.

### Before you update

- Nothing in an agent's code. A PC set up before keeps its models until its local AI is set up again (Manor's Set up local AI). That run downloads the 2B (1.55 GB with its projector) and, on the Adreno, a build from b11715 on. The keeper's look never downloads, so it doesn't make this change by itself.
- Code that read `ModelFile.kind` from `findModels` or `planSetup`'s `models` reads `model`.

## 1.9.54

**It carries the Steward's kit 2.82.4: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.82.4: Each agent's PowerShell worker stays 4 minutes with nothing to run, not 2, so agents start PowerShell less often.

### Before you update

- Nothing: it updates itself as usual.

## 1.9.53

**It carries the Steward's kit 2.81.0: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.81.0: On the Snapdragon X2, the keeper puts Castellan's build of GenieX 0.8.0's llama.cpp plugin in place of GenieX's own, which holds about 2 GB less with Qwen3-VL-2B loaded: its working set after a chat 1.77 GB instead of 4.0, cold load 2.4 s instead of 5.0. It goes in only over GenieX 0.8.0's own file, in the NPU's turn, and stays only once a picture and a chat answer through it; anything else, and GenieX's own stays or goes back.

### Before you update

- Nothing for an agent. Only the keeper (the Smith, else Reeve) changes the file. Every agent's kit carries the build (295 KB) in `src\kit\geniex\`.

## 1.9.52

**It carries the Steward's kit 2.78.22: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.78.22: seneschal.ts knows the Seneschal's other PCs (Seneschal 0.2.0): a task running on another PC isn't timed out, results name the PC they ran on and say flaky or confirmed, a failure's try on another PC can be waited for, `seneschalWork` is the worker's side, and a whole suite can be cut into shards.

### Before you update

- Nothing for an agent to do: `seneschalTurn` answers as before where no other PC runs its tasks. An agent that switches on its answer should handle `{ failed }`.

## 1.9.51

**It carries the Steward's kit 2.78.12: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.78.12: npu-reports.ts counts a report against the model its generation runs (`routeFor`), so an X2's reports on its 2B count, and its reports on the route's 4B are stale. tools/kit.ts warns when a pin is a kit that has no release. Also brings 2.78.10, which wasn't released on its own.

### Before you update

Nothing: it updates itself as usual.

## 1.9.50

**It carries the Steward's kit 2.77.6: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.77.6: Each accelerator's servers stop when that accelerator has been idle, whatever the others do. Before, a let-go of any accelerator's lock counted as use of all of them, so Reeve indexing on a graphics card kept the NPU's servers loaded, and the NPU kept a card's.

### Before you update

- Nothing: it updates itself as usual. The keeper of a PC that runs on more than one accelerator now stops each one's servers on its own idle time.

## 1.9.49

**Heiward's notifications say Heiward, with its icon, however it was installed.**

### What changed

- Heiward's notifications (new sets of likely duplicates, an automatic cleanup) came under **Windows PowerShell** unless Heiward's own setup had installed it, for example when Castellan installed it. Now Heiward makes its name known to Windows, with its icon, the first time it notifies, wherever it came from, and the icon is on each notification too. In Windows' notification settings you can turn Heiward's notifications on or off without touching any other program's.

### Before you update

Nothing: it updates itself as usual. The first notification after the update makes Heiward's name known to Windows.

## 1.9.48

**A release that can't sign because SimplySign's key provider is missing now says what to do.**

### What changed

- When SimplySign Desktop is running but its key provider isn't there ("The specified CSP could not be found"), building a release now stops at once and says to disconnect and connect SimplySign Desktop again, instead of showing signtool's own message. The failure isn't retried.
- This release also brings 1.9.47, which wasn't released on its own: the Steward's kit 2.74.5.

### Before you update

Nothing: it updates itself as usual.

## Unreleased (its version needs claiming on the main laptop)

**An NPU whose plugin can't set the AI model up no longer ends Heiward's scan: the NPU is checked in a process of its own first, and a scan leaves an NPU that failed its check. An AMD Ryzen AI NPU runs the model. AI matching on the processor works again on PCs where Windows has its own ONNX Runtime.**

### What changed

- Before a scan first uses the NPU with its driver and plugin, Heiward checks it in its own process (`hei probe --device npu`), as it already did for a graphics card. On a Ryzen AI 7 350 (NPU driver 32.0.20102.3930), Windows ML's Vitis AI plugin 1.8.75.0 ended Heiward's process from native code while it compiled the AI model, past Heiward's own fallback to the processor, so the first scan never finished, tried again every hour, and left the NPU's lock held by a process that had ended (Castellan showed the NPU in use by Heiward). Now a check that ends that way is recorded in `ai\npu-checks.json`: scans leave the NPU, for the graphics card once it has passed its check or else the processor, until the NPU's driver, its plugin or Heiward's NPU pack changes, or for a day. (Windows ML tells a process the Vitis AI plugin's version only once the plugin is added to that process, so Heiward adds it before asking. Otherwise the scan and its check saw different versions, and the NPU would have been checked again before every scan.) The check's half-compiled model is deleted, and the NPU's lock it died holding is let go.
- On that Ryzen AI 7 350, the NPU now runs the AI model. The Vitis AI plugin's compiler loads the plugin's own DLL by its short (8.3) path, which Windows can't work out for a standard user under `C:\Program Files\WindowsApps`, so every compile failed and the plugin ended the process, whatever the model. Heiward now loads the plugin through a junction to its folder, `ai\vitisai-plugin`, made without admin rights and pointed again when Windows updates the plugin. The NPU also gets one picture per run: with 8, only the first picture of each run came back right. Its embeddings match the processor's (cosine 0.998 or better), at about 8 ms a picture. A check that failed before this update doesn't hold the NPU back.
- AI matching on the processor ended Heiward's process (0xC0000005) when it was the first thing in that process to use ONNX Runtime, on a PC whose Windows has its own ONNX Runtime in System32 (version 1.17, from a Windows 11 update in October 2026). Heiward loaded that one instead of its own 1.27, and it can't serve Heiward. This is what an NPU that failed its check, or a PC without an NPU, falls back to. Heiward now always loads its own.
- A check that passes leaves the compiled model for the scan, which then loads it instead of compiling it.
- The check also makes sure the NPU's embeddings are right, as a plugin can run the model and still get it wrong without an error, as Vitis AI did at 8 pictures a run. It embeds a run's worth of test pictures and one more on the NPU and on the processor, with the processor running the same FP32 model, and compares each picture's pair. An NPU whose picture is under a cosine of 0.98 fails its check, as one that crashed does: scans leave it until its driver or Heiward's NPU pack changes, or for a day, and its compiled model is deleted. The check's reason says which pictures in a run came back wrong.
- The same check runs before `hei setup` opens the AI model, and before the review page's picture comparison first opens it. When setup copies the NPU pack from the installed Heiward and the plugin ends the copy's trial run, that counts as the check: the copy is kept and the model isn't compiled a second time.
- A check that gives no answer in 10 minutes is ended and counts as failed. On the same PC, Vitis AI's compile once hung instead, every thread waiting, which left the scan waiting for good.
- A probe's output is cut to its last 20 lines in the window and the log: Vitis AI's compiler writes about 800.
- The Microsoft Store version needs Windows 11, version 24H2 or later: Heiward runs on .NET 10, which Microsoft supports on Windows 11 from 24H2. The package's minimum Windows (`MinVersion` in `HEI.Agent/Store/AppxManifest.xml`) was Windows 10 version 2004, and `docs/STORE.md` gave that as the listing's requirement. The Store no longer offers Heiward to an older Windows, and a Store copy already on one gets no more updates from it.

### Before you update

Nothing: it updates itself as usual. The first scan after updating checks the NPU once, which takes as long as compiling the model: a few seconds on a Snapdragon or an Intel NPU, about a minute and a half on an AMD one.

## 1.9.47

**It carries the Steward's kit 2.74.5: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.74.5: The NPU: a PC set up by an older kit gets this kit's command, Intel's NPU 3720 writes about 13% faster and holds less, AMD's runs on a 16 GB Ryzen AI (XDNA 2 verified), a known maker's NPU of a generation setup doesn't know is tried instead of refused, and the benchmark times prompts the server hasn't seen.

### Before you update

Nothing: it updates itself as usual.

## 1.9.46

**A scan starts half the processes it did, one for each new file instead of two, and on an Intel NPU it starts in under a second, not 15 to 18.**

### What changed

- The FFmpeg tools a scan runs for each new file (ffprobe, and ffmpeg or fpcalc when they're run as programs) share Heiward's own hidden console instead of each getting a console host (conhost.exe) of its own: 20 files probed started 41 processes, now 21, and 50 probes take 0.5 s instead of 1.0. The same goes for the git, schtasks, gh and FFmpeg checks Heiward runs itself. The desktop app, which has no console to share, starts them as before.
- git now starts the real git.exe behind Git for Windows' launcher (`Git\cmd\git.exe`), which did nothing but start it: the developer check's git commands were three processes each, and are now one.
- On Intel AI Boost, the compiled AI model is now saved in Heiward's own folder (`ai\openvino-cache-1.7.0`). Intel's OpenVINO 1.7.0 ignored the setting Heiward used, so the folder stayed empty, and every scan compiled the model again unless the NPU driver happened to still hold its own copy. On a Core Ultra 5 225U, opening the NPU took 15–18 s to compile and 0.8 s from the saved one, with the same results.

### Before you update

Nothing: it updates itself as usual. The first scan on the NPU after updating compiles the model once more.

## 1.9.45

**It carries the Steward's kit 2.73.2: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.73.2: One process for every git, not two: a page keeps a windowless console its git and gh can share, and a test's commits start no upkeep.

### Before you update

Nothing: it updates itself as usual.

## 1.9.44

**A folder is known by where it really is, whatever path leads to it: through a folder link, or a drive mounted in a folder.**

### What changed

- A worktree in use is never offered for cleanup when it's reached through a folder link (C:\worktrees pointing to another drive, say): a Claude Code session that worked in it, or a program working in it, keeps it, whichever path they used.
- Worktrees the Steward keeps in its own folder, and folders a scheduled task uses, are recognized by where they really are, so they aren't offered either.
- A folder reached two ways, such as C:\Projects where a Dev Drive is mounted and the drive's own letter, is scanned once, so a photo is never listed twice as two copies of itself. A drive mounted only in a folder, with no letter, is scanned too when every drive is.
- Changes since the last scan are read from the drive a folder is really on. A Dev Drive keeps no change journal, so its folders are walked instead of being read from the wrong drive's journal.
- Folders you list, leave out or keep for scanning on request are matched by where they really are, so a path through a link or a mounted drive counts the same as the drive's own.

### Before you update

- The first scan after updating may walk folders on a drive mounted in a folder, or listed through a link, once more in full: they're now listed by where they really are (C:\Projects as D:\, say). Scans after it are as quick as before.

## 1.9.43

**It carries the Steward's kit 2.69.1: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.69.1: An update is no longer rolled back because the network went down while it was installing.

### Before you update

Nothing: it updates itself as usual.

## 1.9.42

**It carries the Steward's kit 2.68.0: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.68.0: Intel NPUs nobody has measured yet (Meteor Lake, Arrow Lake, Panther Lake, Wildcat Lake) are set up safely: setup measures each PC's NPU, checks that its answers make sense, and falls back quietly when it can't use it.

### Before you update

Nothing: it updates itself as usual.

## 1.9.41

**It carries the Steward's kit 2.67.0: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.67.0: Intel's NPU: three times the text a request may carry, and embeddings on the NPU, so a PC with default settings can search its code.

### Before you update

Nothing: it updates itself as usual.

## 1.9.40

**It carries the Steward's kit 2.66.0: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.66.0: The NPU benchmark: a fixed suite that measures how fast and how well a chip does Castellan's work, for the website's boards; and a count of the tokens agents send, to weigh it.

### Before you update

Nothing: it updates itself as usual.

## 1.9.39

**It carries the Steward's kit 2.65.0: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.65.0: Intel's NPU route, run on a real Lunar Lake: a model that works on the NPU, the runtime it needs brought by setup, and a test request that checks the answer.

### Before you update

Nothing: it updates itself as usual.

## 1.9.38

**It carries the Steward's kit 2.64.0: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.64.0: Fewer programs started for each git command: git's own git.exe, and `ls-remote` over https asked in the agent's process.

### Before you update

Nothing: it updates itself as usual.

## 1.9.36

**It carries the Steward's kit 2.61.0: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.61.0: A handler can stream its reply: a live recording sent to another PC as it is made.

### Before you update

Nothing: it updates itself as usual.

## 1.9.35

**It carries the Steward's kit 2.60.0: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.60.0: The NPU check: one command that tries a maker's NPU route on real hardware and measures it.

### Before you update

Nothing: it updates itself as usual.

## 1.9.34

**It carries the Steward's kit 2.59.0: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.59.0: An urgent fix reaches customers at once, not on the Exchequer's next release day.

### Before you update

Nothing: it updates itself as usual.

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
