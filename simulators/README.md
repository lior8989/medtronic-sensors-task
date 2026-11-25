# Camera and Pointer sensor simulators

Two .NET 10 console projects implement the supplied sensors for the
[backend exercise](Developer-Sensor-Task.md). A shared class library contains
the binary protocol, state machine, CLI parsing, and UDP runtime. No backend or
web UI is implemented here.

## Requirements and startup

- .NET **10 SDK** (no third-party NuGet packages).
- Python 3.10+ only if running the integration tests.

From this directory:

```sh
dotnet build SensorSimulators.slnx --configuration Release --warnaserror
```

Run each sensor in a separate terminal:

```sh
dotnet run --project Sensors/CameraSensor --configuration Release --no-build
dotnet run --project Sensors/PointerSensor --configuration Release --no-build
```

Defaults:

| Participant | Address | UDP port |
|---|---|---|
| Backend | 127.0.0.1 | 7450 |
| Camera | 127.0.0.1 | 8100 |
| Pointer | 127.0.0.1 | 8200 |

Both start in **Setup** and send heartbeats every **250 ms**, even without a
backend. They send no measurements until the backend sends Start. Start in Error
does nothing; clear Error first, then send a new Start.

The backend must bind its UDP socket to port 7450 and use that same socket for
receiving data and sending commands. Discover the current session ID and last
accepted command sequence through heartbeats before sending commands. See
[PROTOCOL.md](PROTOCOL.md) for packet layouts, state transitions, and examples.

Override endpoints with IP literals (not hostnames):

```sh
dotnet run --project Sensors/CameraSensor --configuration Release --no-build -- \
  --listen-address 127.0.0.1 --listen-port 8100 \
  --backend-address 127.0.0.1 --backend-port 7450
```

The Pointer project supports the same options, defaulting to listen port 8200.
Use `-- --help` to display options. IPv4 and IPv6 are supported; both endpoint
addresses must use the same address family. Remote access requires a suitable
listen address and firewall rules. Defaults intentionally bind only to loopback.

## Interactive CLI

| Command | Behavior |
|---|---|
| `error` | Enter Error and stop measurements; heartbeats continue. |
| `clear` | Clear Error to Setup; otherwise leave the state unchanged. |
| `status` | Print state, session ID (hex), and last accepted command sequence. |
| `help` | Print CLI commands. |
| `quit` | Exit. Ctrl+C also exits cleanly. |

There is deliberately no CLI `ready` or `start` command. Only UDP Start enters
Ready. Closing stdin leaves UDP operation running, allowing headless use.
