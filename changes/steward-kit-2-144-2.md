**It carries the Steward's kit 2.144.2: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.144.2: An update's probation watcher outlives the install that started it again: it starts apart from the install's jobs, so the last agents of Manor's update round finish their probation and clear app.prev, rather than being ended with Manor's install.

### Before you update

- Nothing: it updates itself as usual. An agent whose last update left probation.json and app.prev behind clears them at its next update.
