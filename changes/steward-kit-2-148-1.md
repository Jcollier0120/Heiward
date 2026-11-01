**It carries the Steward's kit 2.148.1: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.148.1: A note's label names the staff member who wrote it, in its own word for its notes or for what it did: "summary from the Herald", "explained by the Surveyor", where it said "note from the local AI, unverified" (the owner, 2026-10-31).

### Before you update

- **A test that spells out the old label fails**, plain ("note from the local AI, unverified") or a developer's ("note from the NVIDIA GeForce RTX 4090, unverified", `/unverified/`). A test that compares with `noteLabel(from)` itself passes. A note kept with its label (`label: 'note from the NPU, unverified'` in a fixture) is data, and still passes. The staff tests kit 2.148.1's trial found spelling out the old words (2026-11-01, all 27 staff tried):
  - **Porter**, `test/notes-view.test.ts:241`: a developer's label matches `/unverified/`. It fails until Porter's test is brought up.
  - **Bridgeward**, `test/reports.test.ts:195`: `'note from the local AI, unverified'`. The Steward's kit bump brings it up itself, to `'summary from the Bridgeward'`.
- A staff member whose notes aren't summaries passes its own word or action, in its own release: the Clerk's captions (`'caption'`), the Surveyor's and the Watchman's explanations (`'explanation'`, or `{ did: 'explained' }`), the Bridgeward's reports (`'report'`).
- A label drawn by the agent's own code, not through `noteLabel`, says what it said: bring it to `noteLabel(from, developer, what)`.
