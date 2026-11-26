# Development process

How this project was built, and the author's role in it.

**Author:** Lior Baram.
**Tools:** C# / .NET 10, Visual Studio, GitHub. Generative AI (Claude Code) was used as an
implementation assistant, as the assignment allows. The author directed the work, made the design
decisions recorded below and in [`DESIGN.md`](DESIGN.md#11-decision-log), and reviewed every change
before it was merged.

## 1. Preparation: understanding the assignment

Before any design or code, the author read all instruction documents
([`Developer-Sensor-Task.md`](simulators/Developer-Sensor-Task.md),
[`PROTOCOL.md`](simulators/PROTOCOL.md), [`README.md`](simulators/README.md)) and reviewed the supplied
simulator source (Camera, Pointer, SensorShared). The open points were then worked through one at a
time, to confirm a full understanding of:

- **The system's roles:** both sensors talk to the backend directly over UDP; the simulators play the
  role of the device software. One backend socket (7450) receives data and sends commands.
- **The wire protocol:**
  - The 26-byte big-endian header (magic `SNSR`, version, type, sensor ID, flags, session, sequence,
    payload length) and the exact type-specific packet sizes (35 / 426 / 28 / 27 bytes; 426 is the
    largest valid packet).
  - **Session ID:** a random per-process identity, created at sensor start-up, and unrelated to state
    or "connections". Restarting a sensor creates a new session.
  - **Two independent counters:** the sensor's output sequence (shared by heartbeats and
    measurements, used for ordering and duplicate detection, not time), and the backend's per-sensor,
    per-session command sequence (starting at 1, acknowledged through the heartbeat's "last accepted
    command sequence").
  - **Payloads:** heartbeat (state + last accepted command), Camera image (10×10 RGBA, row-major,
    pixel `(x, y)` at offset `4 * (10y + x)`, alpha always 255), Pointer `(x, y)`, command
    (1 = Start, 2 = Stop).
  - **Message boundaries:** UDP is message-oriented (one datagram = one packet), unlike TCP or serial
    streams. The length field is for validation, not framing.
- **The sensor and system state machines:** Setup / Ready / Error; Initializing / Configuring / Running /
  Faulted, including the fault rule's priority, Ready-only silence faults, and automatic recovery.

## 2. Engineering rules set by the author

Before implementation, the author defined the rules every change had to follow. They are stored as a
project skill, [`.claude/skills/engineering-standards/SKILL.md`](.claude/skills/engineering-standards/SKILL.md),
and enforced through [`CLAUDE.md`](CLAUDE.md):

1. **Design:** SOLID principles, OOP, and known design patterns where they bring a concrete benefit.
2. **Performance:** CPU, runtime memory, external resources and other system parts.
3. **Balance:** neither design nor performance is absolute. Prefer the simplest solution that is well
   designed and performs well; avoid both over-engineering and harmful micro-optimization.
4. **Tests at every level,** from small units up to the whole system.
5. **Documentation** on all public members, and on non-public code where the logic matters; Swagger for
   API endpoints.
6. **Modern practices with a minimal footprint:** modern .NET, and only the technologies the task needs
   (e.g. a simple web UI, no extra frameworks or dev-environment changes).

The author also set a code convention during review: **no `while (true)` loops**. A loop's exit
condition must be visible in its header (`while (!cancellationToken.IsCancellationRequested)`), with
the cancellation token passed per call.

## 3. Way of working

- Design first: every open decision was presented with options and a recommendation, and **decided by
  the author** (see the decision log in [`DESIGN.md`](DESIGN.md#11-decision-log)).
- **One branch per part.** After each branch, the changes were presented for review. Only after the
  author's approval was the branch pushed, a pull request opened and merged into `main`.
- Every pull request description states what changed and how it was verified (build, tests, and for
  the later branches, runs against the real simulators).

| PR | Branch | Content |
|---|---|---|
| [#1](https://github.com/lior8989/medtronic-sensors-task/pull/1) | `chore/engineering-standards` | The author's engineering rules as a project skill + `CLAUDE.md` |
| [#2](https://github.com/lior8989/medtronic-sensors-task/pull/2) | `chore/solution-setup` | Solution, projects, shared build settings, simulators copied in |
| [#3](https://github.com/lior8989/medtronic-sensors-task/pull/3) | `feature/communication` | Packet model, decoder/encoder, UDP transport |
| [#4](https://github.com/lior8989/medtronic-sensors-task/pull/4) | `feature/sensor-control` | Per-sensor session, sequence, freshness and commands |
| [#5](https://github.com/lior8989/medtronic-sensors-task/pull/5) | `feature/system-state-machine` | System state machine and single event loop |
| [#6](https://github.com/lior8989/medtronic-sensors-task/pull/6) | `feature/processing` | Pairing rule and latest-wins processing worker |
| [#7](https://github.com/lior8989/medtronic-sensors-task/pull/7) | `feature/web-api` | Web host, REST + SSE, Swagger, minimal UI |
| [#8](https://github.com/lior8989/medtronic-sensors-task/pull/8) | `fix/simulator-udp-connreset` | Simulator start-order fix + Visual Studio launch profile |
| [#9](https://github.com/lior8989/medtronic-sensors-task/pull/9) | `test/integration` | End-to-end tests with simulated UDP sensors |
| #10 | `docs/design` | README, design document, decision log, this document |

## 4. Changes driven by the author's review

- **Receive loop style:** `while (true)` replaced by a token-controlled loop condition (PR #3), and the
  rule applied to all later loops.
- **Retired sessions:** the author questioned the bound of 32 remembered session IDs and why old IDs
  are kept. The reasoning (late packets must not switch tracking back to a dead session; restarts are
  rare, so the bound is cheap) was confirmed and kept (PR #4).
- **Packet types:** the author asked for the trade-off between structs and record classes. The result
  is records for the packet hierarchy and structs for small value types (PR #3).
- **Simulators:** after the end-to-end run showed that the supplied simulators exit on Windows if
  started before the backend, the author decided to apply one small, isolated fix to them, so that
  all projects can be started together, and to keep the rule that nothing else in them changes (PR #8).
- **Running everything together:** the author asked for a simple way to start the whole system. The
  result is the Visual Studio launch profile `Backend + Camera + Pointer`, verified by the author.

## 5. Review status

- Every branch was reviewed by the author before its merge, and the author ran the full system with the
  simulators from Visual Studio.
- Planned follow-up: a walkthrough session in which the author goes through `Sensor.Control`
  (`Supervision/`, `Processing/`) and `Sensor.WebApi` in detail, to discuss specific implementation
  points.
