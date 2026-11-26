# Project instructions

- Before adding, editing, or deleting any code, load and follow the `engineering-standards`
  skill (`.claude/skills/engineering-standards/SKILL.md`).
- Work one branch per part. Show the changes for review; push, open a PR, and merge to `main`
  only after approval.
- The supplied simulators under `simulators/` must not be modified, except the one approved local
  fix: `SimulatorApp.DisableUdpConnectionReset` (Windows `SIO_UDP_CONNRESET`, so a simulator no longer
  exits when it sends before the backend is listening). The backend must keep working with the
  original, unmodified simulators.
