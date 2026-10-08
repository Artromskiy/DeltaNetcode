# DeltaNetcode agent router

Scope: engine-independent networking for .NET sessions. The runtime provides
typed command processing, local/client/server session routing, prediction,
runtime dispatch visible on hot paths.

## Map — open only as needed

- ../CODE_STYLE.md — hot-path, ownership, allocation and API rules.
- ../CONTRACTS.md — cross-project boundaries; open only for integration work.
- WORKFLOW.md — layout, build and shared pre-commit checks.
- README.md — public overview; open for documentation or quick-start work.
- docs/API.md — public session and command behavior.
- docs/PROTOCOL.md — encoded frame layout and ownership.
- src/DeltaNetcode — primary library project.
- tests, benchmarks, samples and probes — verification, measured workloads,
  runnable examples and bounded capability checks.

Do not add engine dependencies to the primary library. Keep transport-specific
implementations separate from the engine-independent core when a concrete
transport requires them.
