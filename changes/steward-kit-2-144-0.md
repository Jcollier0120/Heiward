**It carries the Steward's kit 2.144.0: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.144.0: While a button's action runs, the whole page waits: the button pressed spins, and every control on the page is disabled until the action is done, has failed or has timed out, then focus is back where it was. A page's own button whose handler awaits `post()` gets it too.

### Before you update

- Nothing for a page whose actions are `PostButton`s, kit `Button`s whose `onPress` returns its promise, `BusyButton`s, or handlers that `post()`: from this kit on, the whole page waits on them. A page whose own code sends an action with `fetch` gets none of it: send it with `post()`, or hold the page with `holdPage()` until its request answers (a streamed answer, say). A dialog of the page's own making that an action opens while it runs waits with the page: make it a `ModalCard`. A handler that asks with the browser's `confirm()` still works; `confirmDialog` is the page's own.
