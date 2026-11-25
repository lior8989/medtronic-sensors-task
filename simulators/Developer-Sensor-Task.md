# Developer Sensor Task

**Language:** C# for the backend.  
**Generative AI:** Allowed. You must understand and be able to explain your submission.

## Objective

Build a backend system that receives UDP messages from two supplied sensor simulators, tracks system state, processes measurements, and exposes live information through a minimal web UI.

Prioritize correctness and clear engineering decisions over UI polish. The web UI may use any practical technology. Styling is not assessed.

Read this brief together with the [simulator startup instructions](README.md) and the [protocol](PROTOCOL.md). Those documents define the wire format and the supplied sensors. 

## Provided materials

- Camera and Pointer .NET 10 command-line simulators, including source and [startup instructions](README.md).
- A documented [custom binary UDP protocol and example packets](PROTOCOL.md).
- Support for state heartbeats, measurements, and Stop and Start commands.
- Protocol definitions for sensor identity, session identity, sequence numbers, byte order, version, and payload validation.


By default, the backend binds to UDP port **7450**. Camera and Pointer bind to ports **8100** and **8200** on localhost. Send commands from that same bound socket. The simulators reject commands from any other source.

## Sensor behavior

The simulators already implement this behavior. It is described here so the backend can be tested against it.

Both sensors have three states:

- **Setup**
- **Ready**
- **Error**

Each sensor starts in **Setup** and sends no measurements until it accepts a Start command.

They send state heartbeats every **250 ms**, including in Setup and Error, and before the first Start command. A valid heartbeat is traffic. Lack of measurements in Setup or Error is normal.

While Ready, they send measurements at these rates:

| Sensor | Measurement | Rate |
|---|---|---|
| Camera | A random 10×10 color image | 50 Hz |
| Pointer | A pixel position `(x, y)` in that image | 10 Hz |

The simulators do not synchronize these streams. Rates are best-effort.

Each Camera measurement contains 100 pixels in 10 rows and 10 columns, row-major, top row first. Each pixel is **4 bytes** in **R, G, B, A** order. A is alpha always **255**. 

Each Pointer measurement contains two coordinates, `x` then `y`. The origin `(0, 0)` is the top-left pixel. `x` increases to the right and `y` increases downward.

The operator can set a sensor to **Error**, or clear an Error, through its CLI. Clearing an Error moves that sensor from Error to **Setup**, not Ready. 

Only a backend **Start** command can move a sensor from Setup to Ready and start measurements. 

A **Stop** command moves a Ready sensor to Setup and stops measurements. 

After startup, Stop, or clearing an Error, the sensor must accept a new Start command before it can enter Ready and send measurements. A Start that was accepted before that event is not reused.

## Processing

Return the color of the Camera pixel at the position reported by the Pointer:

The result is an RGBA color.

Which Camera image to combine with which Pointer position is your decision. The streams are not paired by the simulators. Document the rule, including what you do when one stream is newer than the other, when a sample is from an older session, and when you consider a sample too old to use.

The production algorithm is computationally expensive; this exercise uses the simple pixel lookup above. Take this into account in the design.


## System States

| State | Meaning |
|---|---|
| **Initializing** | Waiting to hear valid traffic from both sensors for the first time. |
| **Configuring** | Both sensors have been seen, and at least one is in Setup. |
| **Running** | Both sensors are Ready. Process measurements under your selection rule. |
| **Faulted** | A sensor reports Error, or a Ready sensor sends no valid traffic for 1 second. |

Valid traffic is a valid heartbeat or a valid measurement for the sensor's current session, as defined in [PROTOCOL.md](PROTOCOL.md). Invalid, malformed, or stale packets do not refresh freshness and do not change state.

### Transition rules

These rules apply only in the state they name, except the fault rule, which applies in every state and takes priority.

- Stay **Initializing** until both sensors have been seen. There is no timeout for a sensor that has never been seen. Do not send Start in Initializing.
- Once both sensors have been seen, enter **Configuring** if either is in Setup, or **Running** if both are Ready.
- In **Configuring**, send Start to each sensor that is reporting Setup. Retry Start while it keeps reporting Setup. Command delivery is best-effort UDP, so a retry must be a new command under the protocol sequence rules, not a duplicate packet. Enter **Running** only after a fresh Ready report from both sensors. Do not apply this procedure while Faulted.
- Enter **Faulted** from any state if either sensor reports Error, or if a sensor whose latest accepted state is **Ready** sends no valid traffic for **1 second**. Silence does not fault a sensor that has never reported Ready. Silence while the latest accepted state is Setup or Error does not, by itself, enter Faulted. An Error report still faults immediately.
- A restarted sensor begins a new session in Setup. The gap after a Ready session stops sending can still be the 1-second Ready silence fault. Setup heartbeats from the new session are not themselves a silence fault.
- Stay **Faulted** until the recovery conditions below are met. A sensor left in Setup during recovery does not make the system Configuring.

If a fault occurs before the other sensor has ever been seen, recovery still waits until both sensors are connected and reporting Ready. The "no timeout until seen" rule does not drop the unseen sensor from recovery.

## Fault Handling and Automatic Recovery

On entering Faulted:

1. Stop processing.
2. Send a best-effort UDP Stop command to the other sensor for a sensor-specific fault. 
3. Log send failures; remain Faulted regardless of command delivery.
4. Do not repeatedly send Stop for repeated reports of the same fault episode.

Recovery is automatic:

- The operator clears a sensor's Error through its CLI, returning it to Setup. For a disconnected sensor, the operator restores connectivity or restarts it; a restarted sensor also begins in Setup.
- Once a faulted sensor is connected and reports Setup after its fault, the backend sends it a Start command. Start must not clear an Error.
- Once the faulted sensors are connected and report Ready, the backend sends a Start command to any peer it stopped.


## Minimal Web UI

Display live:

- System state and fault reason.
- Each sensor's state and connection status.
- Latest raw image and pointer
- Latest calculated pixel color (`R`, `G`, `B`, `A`, with `A = 255`) and the Camera image and Pointer position used.

## Submission

Provide:

- Working, runnable source, and instructions for starting the backend with the supplied simulators.
- A design document explaining your assumptions, the decisions you made.
- Automated tests for the behavior you implement.

