**It carries the Steward's kit 2.145.0: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.145.0: Every block of output has a copy button at its top-right: `Output` carries one, and `CopyButton` puts one in a page's own `<pre>`. One click copies the whole block, the lines scrolled out of sight too.

### Before you update

- A page with its own `<pre>` of output (a log, a command's lines, a report to be sent) puts `<CopyButton text={…} label="…" />` first in it, or uses `Output`. A page that copies the kit's CSS by hand (Manor's app.css) copies the `button.ui-copy` rules too.
