**It carries the Steward's kit 2.146.1: the parts every agent of the manor shares.**

### What changed

- The Steward's kit 2.146.1: git-auth.ts says what a runner does with `env`: it lays it over the command's own environment, never runs a command with it alone. The sign-in's git settings are numbered after the ones that environment already has.
- The Steward's kit 2.146.0: A repository may be on any host, or on none: GitHub, GitHub Enterprise, GitLab (subgroups and self-hosted too), Bitbucket, Azure DevOps, Gitea, Forgejo and Codeberg, any other git server, or a folder on this PC. And an agent that is an MCP server can connect itself to ten AI assistants, adding only its own entry to each one's settings.

### Before you update

- Nothing for a runner that spawns with `{ ...process.env, ...opts.env }`, as the Steward's and Reeve's do. A runner whose commands get an environment of their own (a session's) passes that environment whole in `env` to the runner `signedIn` wraps, its GIT_CONFIG_COUNT included, and lays the `env` `signedIn` hands back over that same environment.
- Nothing for an agent on GitHub: its projects, staff.json and results read exactly as before, and a project has no `host` key unless its entry gives one. An agent that builds GitHub URLs from a project's `repo` should use `projectOrigin(p).webUrl` instead, or check that the repo is owner/name first, because it may now be host/path. A page that copies the kit's CSS by hand copies the `.ui-assistant` rules too.
