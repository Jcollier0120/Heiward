**It carries the Steward's kit 2.143.3: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.143.3: A page left open while its agent updated, or restarted, saves again: its server starts with a new token each time, so the tab's POSTs were refused, and Save said only "forbidden" until the tab was reloaded.
- The Steward's kit 2.143.2: The keeper's looks no longer set a lock folder's time against the clock: on Windows a folder's time often runs a millisecond or two ahead of `Date.now()`, so a slot taken just before a look could make the next look count the shared folder's next change, an agent's work turn say, as this accelerator's let-go.
- The Steward's kit 2.143.1: A call Envoy captions on the NPU comes first: the staff's background model work waits for the call's end, and when the call doesn't translate, the NPU's servers stop once unused for two minutes. Beside the NPU's chat model answering, Envoy's speech slowed (Whisper small's decoder steps tripled on a Ryzen AI 7 350), and on AMD the NPU itself timed out.

### Before you update

- Nothing for a page that POSTs with `post` (its Settings form and its PostButtons do): a tab opened from this kit on recovers by itself; one opened before needs a reload once. A page whose own code POSTs with `pageToken()` and its own fetch gets none of this until it uses `post`, or `freshToken` on a 403 `forbidden`.
- Nothing: it updates itself as usual.
- Nothing for most agents: it updates itself as usual. Background work on the NPU now waits while Envoy captions a call, so a round during a call defers its model work to a later round. Envoy writes `npu-call.json` from its next release on this kit; until then, nothing changes. Reeve's own routing reads the file from its own release.
