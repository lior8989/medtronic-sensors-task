# Sensor Backend

A C# (.NET 10) backend that receives UDP traffic from the supplied **Camera** and **Pointer**
simulators, tracks sensor and system state, starts/stops the sensors, pairs measurements, calculates
the pointed pixel's color, and shows everything live in a minimal web UI.

- Task, protocol and simulator docs: [`simulators/`](simulators/) (`Developer-Sensor-Task.md`, `PROTOCOL.md`, `README.md`)
- Design, assumptions and decision log: [`DESIGN.md`](DESIGN.md)
- How the project was built and the author's role: [`DEVELOPMENT-PROCESS.md`](DEVELOPMENT-PROCESS.md)

## Quick start (for reviewers)

Requires only the .NET 10 SDK. From the repository root, build once:

```sh
dotnet build Sensors.slnx --configuration Release
```

Then start these in **three terminals** (any order):

```sh
dotnet run --project src/Sensor.WebApi --configuration Release --no-build
dotnet run --project simulators/Sensors/CameraSensor --configuration Release --no-build
dotnet run --project simulators/Sensors/PointerSensor --configuration Release --no-build
```

Open **http://localhost:5080**. The system reaches **Running** within a second. Type `error`, then
`clear`, in a simulator's console to see a fault and the automatic recovery. Run the tests with
`dotnet test Sensors.slnx`. More options are described below.

## Requirements

- .NET **10 SDK** or later (built and tested with SDK 11.0.100 targeting `net10.0`). Nothing else.

## Build and test

```sh
dotnet build Sensors.slnx --configuration Release
dotnet test  Sensors.slnx --configuration Release
```

The test suite (unit, endpoint and end-to-end over UDP loopback) takes about 10 seconds.

## Run

The backend serves the UI itself; there is no separate frontend process.

| What | URL |
|---|---|
| Live UI | http://localhost:5080 |
| Swagger UI | http://localhost:5080/swagger |
| Status (JSON) | http://localhost:5080/api/status |
| Live stream (SSE) | http://localhost:5080/api/events |

### Option 1: Visual Studio (all together)

1. **File → Open → Project/Solution…** and open `Sensors.slnx` (not "Open Folder").
2. Select the launch profile **Backend + Camera + Pointer** and start.
   Three consoles open: the backend, the Camera and the Pointer (type CLI commands there).
3. Open http://localhost:5080.

### Option 2: Command line (three terminals)

```sh
dotnet build Sensors.slnx --configuration Release

# Terminal 1: backend (UDP 7450, HTTP 5080)
dotnet run --project src/Sensor.WebApi --configuration Release --no-build

# Terminal 2 and 3: simulators
dotnet run --project simulators/Sensors/CameraSensor  --configuration Release --no-build
dotnet run --project simulators/Sensors/PointerSensor --configuration Release --no-build
```

With the simulators in this repository the start order does not matter (see the note below).

### Trying the scenarios

Type these into a simulator's console:

| Command | Expected in the UI |
|---|---|
| `error` (Camera or Pointer) | System **Faulted**, reason "… reported Error"; the other sensor is stopped (Setup) |
| `clear` | Automatic recovery: the cleared sensor is started, then the other one; system **Running** |
| `quit`, then start it again | Silence fault (> 1 s while Ready), then the new session is started; **Running** |
| `status` | Prints the simulator's state, session and last accepted command |

### Note on the supplied (original) simulators

On Windows, the original simulators exit with `UDP failure: An existing connection was forcibly closed`
if they send a heartbeat while nothing listens on 7450, because an ICMP "port unreachable" surfaces as
a reset on their next receive. **With the original simulators, start the backend first**, and restart
them if the backend is restarted. The copy in this repository has one small fix for this
(`SimulatorApp.DisableUdpConnectionReset`); nothing else in the simulators was changed. The backend
itself works with either version.

## Configuration

`src/Sensor.WebApi/appsettings.json` (override with environment variables or command-line arguments,
e.g. `--Sensors:BackendEndpoint=127.0.0.1:7451`):

| Setting | Default | Meaning |
|---|---|---|
| `Sensors:BackendEndpoint` | `127.0.0.1:7450` | The backend's single UDP socket (receive + send commands) |
| `Sensors:CameraEndpoint` | `127.0.0.1:8100` | Camera command endpoint; only accepted Camera source |
| `Sensors:PointerEndpoint` | `127.0.0.1:8200` | Pointer command endpoint; only accepted Pointer source |
| `Supervisor:SilenceTimeout` | `00:00:01` | Ready sensor silence that faults the system |
| `Supervisor:StartRetryInterval` | `00:00:00.500` | Start retry interval while a sensor keeps reporting Setup |
| `Supervisor:TickPeriod` | `00:00:00.050` | Timer tick for silence detection and retries |
| `Pairing:MaxImageAge` | `00:00:00.050` | Max image age relative to the Pointer sample for pairing |

The HTTP port is set in `src/Sensor.WebApi/Properties/launchSettings.json` (or `--urls`).

## Repository layout

```
src/
  Sensor.Communication   protocol model, packet decoder/encoder, UDP transport
  Sensor.Control         SensorControl (per sensor), SystemSupervisor + event loop, pairing, processing, status
  Sensor.WebApi          ASP.NET Core host: composition, endpoints, Swagger, static UI (wwwroot)
tests/
  Sensor.Tests           unit, endpoint and end-to-end tests (xUnit v3)
simulators/              supplied Camera/Pointer simulators and task documents
```
