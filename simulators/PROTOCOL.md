# Sensor UDP Protocol v1

This protocol is used by the Camera and Pointer .NET 10 simulators. Each UDP
datagram contains exactly one packet. All multi-byte integers are **unsigned,
big-endian (network byte order)**. There is no padding, compression, or text
encoding in payloads.

## Transport and defaults

| Participant | Address | UDP port |
|---|---|---|
| Backend | 127.0.0.1 | 7450 |
| Camera | 127.0.0.1 | 8100 |
| Pointer | 127.0.0.1 | 8200 |

Sensors send heartbeats and measurements from their bound command port to the
backend. The backend must send commands **from its data-receiving address and
port**, using the same socket, to each sensor's command port. Sensors reject
commands from any other source endpoint. Addresses and ports are configurable;
see [startup instructions](README.md).

UDP can lose, duplicate, and reorder packets. There are no separate acknowledgments.
An accepted command triggers an immediate heartbeat; regular heartbeats continue
every 250 ms in every state. All rates are best-effort, subject to OS scheduling.
Missed measurement timer ticks are coalesced, not replayed as a backlog.

This is a trusted-network exercise, not an authenticated or encrypted protocol.
Endpoint checks and session IDs are not a substitute for authentication.

## Header (26 bytes)

Offsets are zero-based. The same layout is used in both directions.

| Offset | Bytes | Field | Value / meaning |
|---|---|---|---|
| 0 | 4 | Magic | ASCII `SNSR`, hex `53 4E 53 52` |
| 4 | 1 | Version | `1` |
| 5 | 1 | Message type | `1` Heartbeat, `2` Measurement, `3` Command |
| 6 | 1 | Sensor ID | `1` Camera, `2` Pointer; target sensor for commands |
| 7 | 1 | Reserved flags | Must be `0` |
| 8 | 8 | Session ID | Nonzero random sensor process/boot identity |
| 16 | 8 | Sequence | Nonzero sequence number; see below |
| 24 | 2 | Payload length | Exact payload byte count, excluding header |
| 26 | variable | Payload | Defined by type and sensor |

Every sensor process creates a new cryptographically random 64-bit nonzero session
ID at startup. It stays fixed until process exit. Commands must echo the target
sensor's **current** session ID from a heartbeat. Commands from an earlier boot
are rejected.

### Sequence numbers

- **Sensor output:** a single counter shared by heartbeats and measurements,
  starting at 1 for each session. Every attempted send consumes the next number,
  even if sending fails. Gaps are allowed; duplicates and older packets should
  not replace newer state or measurements within the same session.
- **Backend commands:** a separate increasing counter for each sensor session,
  starting at 1. The sensor accepts only numbers greater than its last accepted
  command sequence. Invalid packets do not advance this counter.
- Every heartbeat includes the last accepted command sequence (initially 0).
  A restarted backend should read a fresh heartbeat and choose a command sequence
  greater than that value and any it already sent. Use a single command writer
  per sensor.
- Even a valid command that leaves the state unchanged consumes its command
  sequence. To retry, send the same command with a **higher sequence**. Exact
  duplicates are rejected and do not trigger immediate heartbeats.
- Neither counter wraps; restart the sensor before exhausting unsigned 64-bit
  sequences. On a new sensor session, reset sequence tracking and discard cached
  measurements from the old session. Retire the old session so delayed old packets
  cannot switch tracking back to it.

## Heartbeat (type 1)

Payload length: **9 bytes**. Packet length: **35 bytes**.

| Payload offset | Bytes | Meaning |
|---|---|---|
| 0 | 1 | State: `0` Setup, `1` Ready, `2` Error |
| 1 | 8 | Last accepted backend command sequence, or `0` if none |

The sensor sends a heartbeat immediately on startup, every 250 ms thereafter,
after every accepted command (even if no state change), and after CLI `error` or
`clear`. These extra heartbeats do not replace the regular schedule.

State heartbeats confirm the resulting state, not successful delivery of any
particular measurement. A Start accepted while in Error is visible in the command
sequence but the reported state remains Error.

## Camera measurement (type 2, sensor 1)

Payload length: **400 bytes**. Packet length: **426 bytes**.

- Image size is fixed at 10 columns by 10 rows; dimensions are not transmitted.
- Pixels are row-major: left-to-right within each row, top row first.
- Each pixel is four consecutive bytes in **R, G, B, A** order.
- R, G, and B are independently random values from 0 to 255 for each new image.
- A is always **255** (fully opaque).
- The pixel `(x, y)` begins at payload offset `4 * (10 * y + x)`.

While Ready, a new image is sent every **20 ms (50 Hz)**.

## Pointer measurement (type 2, sensor 2)

Payload length: **2 bytes**. Packet length: **28 bytes**.

| Payload offset | Bytes | Meaning |
|---|---|---|
| 0 | 1 | x, one unsigned byte, independently random from 0 to 9 |
| 1 | 1 | y, one unsigned byte, independently random from 0 to 9 |

`(0, 0)` is the top-left pixel; x increases rightward and y downward.
While Ready, a new position is sent every **100 ms (10 Hz)**.

The backend's processing result is the four RGBA bytes at the selected Camera
image's pixel `(x, y)`, including alpha 255. `x` and `y` are the single unsigned
bytes in the Pointer payload, each from 0 to 9. The simulators do not synchronize
measurements and do not define which image to combine with which position. That
selection, and how freshness is judged, is a backend design choice described in
the [exercise](Developer-Sensor-Task.md).

## Commands (type 3)

Payload length: **1 byte**. Packet length: **27 bytes**.

| Value | Command |
|---|---|
| 1 | Start |
| 2 | Stop |

### State transitions

Each sensor starts in **Setup**, sends heartbeats immediately, and sends **no
measurements** before a valid Start command.

| Event | Setup | Ready | Error |
|---|---|---|---|
| UDP Start | Ready | Ready | Error |
| UDP Stop | Setup | Setup | Error |
| CLI `error` | Error | Error | Error |
| CLI `clear` | Setup | Ready | Setup |

Only UDP Start can enter Ready. Stop and Error immediately disable further
measurement generation; already-sent UDP packets can still arrive afterward.
CLI `clear` changes only Error to Setup. In Setup or Ready it leaves the state
unchanged. Clearing Error does not start measurements or replay a previously
accepted Start. A **new Start with a higher command sequence** is required.

The CLI also supports `status`, `help`, and `quit`; none enters Ready.
Closing stdin leaves the sensor running headlessly. Ctrl+C or `quit` exits.
Send failures are logged and sending is attempted again on subsequent ticks.
Fatal socket/bind or console I/O failures cause a nonzero process exit.

## Validation

Reject and log malformed commands without changing state or command sequence.
Sensors validate the source endpoint, exact datagram length, magic, version, type,
sensor ID, reserved flags, current session, nonzero/increasing command sequence,
declared payload length, and known command value.

The backend should validate equivalent header fields for incoming sensor packets,
the exact type-specific payload length, known state values, valid coordinates,
and alpha 255. Reject trailing bytes and truncated datagrams. Invalid, malformed,
or stale traffic must not refresh connection freshness and must not change the
accepted sensor state.

A valid heartbeat or measurement for the current session is valid traffic and
refreshes freshness, including in Setup and Error. A lack of measurements in
Setup or Error is normal. Packets from a retired session must not refresh the
current session or switch tracking back to the old one.

This protocol does not define the backend system state machine. The exercise
does: silence is a system fault only while that sensor's latest accepted state
is Ready, and only after 1 second without valid traffic. Silence in Setup or
Error does not, by itself, fault the system. An Error report still does.

## Example packets

Spaces and line breaks below are for readability only, not bytes on the wire.
Examples use the sensor session ID `0x0102030405060708`.

### Example: Camera Setup heartbeat

```hex
534E5352 01 01 01 00 0102030405060708 0000000000000001 0009
00 0000000000000000
```

### Example: Camera Start command

```hex
534E5352 01 03 01 00 0102030405060708 0000000000000001 0001
01
```

### Example: Camera Ready heartbeat

```hex
534E5352 01 01 01 00 0102030405060708 0000000000000002 0009
01 0000000000000001
```

### Example: Pointer measurement at x=3 y=7

```hex
534E5352 01 02 02 00 0102030405060708 0000000000000003 0002
03 07
```

### Example: Camera Stop command

```hex
534E5352 01 03 01 00 0102030405060708 0000000000000002 0001
02
```

For a complete deterministic Camera measurement example (all pixels opaque red),
this Python code generates all 426 bytes. Real simulator images are random:

```python
import struct

packet = struct.pack(
    "!4sBBBBQQH", b"SNSR", 1, 2, 1, 0, 0x0102030405060708, 3, 400
) + bytes([255, 0, 0, 255]) * 100
print(packet.hex(" "))
```
