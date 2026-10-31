**It carries the Steward's kit 2.147.4: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.147.4: A fetch or push to Codeberg, or another host Git Credential Manager signs in to by OAuth, works again once its sign-in is an hour old. GCM handed back its expired access token, the host refused it, and since 2.146.6 nothing told GCM to make a new one.

### Before you update

- Nothing to do. An agent whose runner can't start `cmdkey` (it isn't on PATH) gets nothing from it, and the refusal stands as before.
