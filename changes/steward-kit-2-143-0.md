**It carries the Steward's kit 2.143.0: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.143.0: Every agent's About page opens with What's new: its own changelog's entries for the versions its latest updates brought, newest first, so a person sees what changed after an update that moved through several versions, not just the last one's notes.

### Before you update

- Nothing: it updates itself as usual. The section appears from the agent's first release on this kit, which is the first to carry its changelog; until its next update it shows that version's entry, or the versions since the one in `app.prev`. An agent with its own route named `/api/whats-new` must rename it (none has one). Reeve's and Manor's own release scripts don't carry the changelog yet, so their About shows no What's new until they do.
