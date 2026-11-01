**It carries the Steward's kit 2.148.0: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.148.0: While the Watchman rolls back a graphics card's driver, or a new driver is being installed on it, no model work goes to that card until it's done. The other cards and the processor go on.
- The Steward's kit 2.147.5: A refused sign-in on gitlab.com no longer takes the whole GitLab sign-in with it. The kit forgets a sign-in only where Git Credential Manager keeps its refresh token as `refresh_token.<host>`, shown stale or not.

### Before you update

- Nothing for an agent that routes through the kit (`Npu`) and keeps its servers with the kit's keeper (the Smith, Reeve): the change comes with the kit.
- An agent that shows its accelerators' state can show `driver` (`say.driverWaiting`) where it shows a failure. Manor's accelerator card does, in a release of its own.
- Not covered: a program that runs its model on a card in its own process (Heiward's DirectML). The guard's own 20-second wait, and its stopping the manor's servers on the card, still apply to a server an older kit started.
- Nothing to do. A sign-in shown stale with no `refresh_token.<host>` beside it, or off Windows, is no longer forgotten. The refusal stands until the person signs git in again, and nothing in the kit says so beyond git's own message.
