---
name: engineering-standards
description: Mandatory engineering rules for this repository. Use BEFORE adding, editing, or deleting any code, tests, or project files in the sensor backend solution (Sensor.Communication, Sensor.Control, Sensor.WebApi, Sensor.Tests). Covers SOLID/OOP/design patterns, performance, the balance between them, testing, documentation/Swagger, and modern-but-minimal technology choices.
---

# Engineering Standards

Apply every rule below to **all** code changes (added, edited, or deleted). The rules are
ordered, but rule 3 (balance) governs how rules 1 and 2 are applied.

## 1. Design: SOLID, OOP, design patterns

- **S**ingle responsibility: one reason to change per class. Split parsing, I/O, state, and
  presentation into separate types.
- **O**pen/closed: extend through new types or injected strategies, not by editing switch
  statements spread across the codebase.
- **L**iskov: implementations must honor the full contract of their abstraction (including
  exceptions, threading, and cancellation behavior).
- **I**nterface segregation: small, focused interfaces. No "god" interfaces.
- **D**ependency inversion: depend on abstractions at seams that need substitution
  (I/O, time, transport). Wire dependencies through constructor injection / the DI container.
- OOP: encapsulate state (private fields, minimal public surface, immutable where possible —
  `readonly`, `record`, `init`). Prefer composition over inheritance.
- Use a known design pattern **only when it gives a concrete benefit** (e.g. State for the
  system state machine, Strategy for pairing/processing, Adapter for transport). Name the
  pattern in the type's XML doc when one is used.

## 2. Performance

- Consider CPU, allocations/GC pressure, memory footprint, threads, sockets, and other
  external resources for every change.
- Hot paths (UDP receive, parsing, pairing) must avoid per-packet allocations where practical:
  reuse buffers, use `Span<T>`/`ReadOnlySpan<T>`/`Memory<T>`, `BinaryPrimitives` for
  big-endian fields, `readonly struct` for small value types.
- Never block threads on async work (no `.Result`, `.Wait()`); propagate `CancellationToken`.
- No busy-wait or polling loops; wait on signals (`Channel<T>`, `PeriodicTimer`, awaits).
- Bound all buffers and queues (e.g. `Channel` with capacity + drop policy). No unbounded growth.
- Dispose resources deterministically (`using`, `IAsyncDisposable`).
- Use a monotonic clock (`TimeProvider` / `Stopwatch` timestamps) for timeouts and freshness,
  never `DateTime.Now`.

## 3. Balance between design and performance

- The system must be **well designed and perform well**. Neither rule 1 nor rule 2 is absolute.
- Do not add abstractions, layers, or patterns that cost noticeable performance or complexity
  without a concrete benefit (testability, a real second implementation, clear separation).
- Do not micro-optimize in a way that harms readability or design when the gain is negligible
  for the actual load (here: ~60 packets/s).
- **Prefer the simplest solution** that is correct, testable, and fast enough. No speculative
  generality (YAGNI). An interface needs a reason: a test double, a real alternative, or a
  boundary (I/O, time).

## 4. Testing

- Every part of the system gets automated tests, from small units up to the whole system:
  - **Unit tests**: codec, validation, sequence/session tracking, state machine transitions,
    pairing rule, worker behavior. Use fakes for transport and `FakeTimeProvider` for time.
  - **Integration tests**: backend against fake UDP sensors over real loopback sockets.
  - **Endpoint tests**: HTTP endpoints via `WebApplicationFactory`.
- Framework: **xUnit v3**. Test names describe behavior: `Method_Condition_ExpectedResult`.
- Tests must be deterministic (no real sleeps for timing logic; control time via `TimeProvider`).
- Run the full test suite (`dotnet test`) after every change and report the real result.

## 5. Documentation

- XML doc comments (`///`) on **all public** members.
- Also document private, protected, and internal members, and comment code blocks, **when the
  logic is important or non-obvious** (protocol rules, state transitions, thread-safety, why a
  choice was made). Do not add comments that only repeat the code.
- Enable `GenerateDocumentationFile` so missing public docs surface as warnings.
- HTTP endpoints must be described in OpenAPI (built-in `Microsoft.AspNetCore.OpenApi`) and
  browsable via Swagger UI (`Swashbuckle.AspNetCore.SwaggerUI`), with summaries, descriptions,
  and response types.

## 6. Modern practices, minimal footprint

- Target **.NET 10 / C# latest**: nullable reference types enabled, file-scoped namespaces,
  primary constructors where they improve clarity, minimal APIs, `BackgroundService`,
  `IOptions<T>` for configuration, built-in `ILogger` structured logging.
- Treat warnings as errors in build.
- **Install only what the task needs.** Prefer built-in .NET libraries. Every new NuGet package
  or tool needs a clear justification against the task requirements (Developer-Sensor-Task.md,
  PROTOCOL.md, README.md). The web UI stays simple: static HTML/JS/CSS in `wwwroot`, no
  frontend framework or Node toolchain.
- Do not change the dev environment (global tools, SDKs, system settings) unless required.
- The supplied simulators under `simulators/` are reference material: **do not modify them**.

## Checklist before presenting a change

1. SOLID/OOP respected; any pattern used has a stated benefit.
2. No needless allocations, blocking, polling, or unbounded buffers on hot paths.
3. Simplest adequate design — no speculative abstractions.
4. Tests added/updated and `dotnet test` passes.
5. Docs/comments added where required; endpoints documented in Swagger.
6. No unnecessary packages or environment changes.
