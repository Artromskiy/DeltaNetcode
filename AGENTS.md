# DeltaNetcode agent router

Scope: engine-independent, high-performance networking for .NET. Keep data
ownership, buffer movement, allocation and runtime dispatch visible on hot
paths. This repository is a scaffold; establish public APIs and transport
boundaries from concrete consumers rather than speculative layers.

## Map — open only as needed

- ../CODE_STYLE.md — hot-path, ownership, allocation and API rules.
- ../CONTRACTS.md — cross-project boundaries; open only for integration work.
- WORKFLOW.md — layout, build and shared pre-commit checks.
- README.md — public overview; open for documentation or quick-start work.
- src/DeltaNetcode — primary library project.
- tests, benchmarks, samples and probes — verification, measured workloads,
  runnable examples and bounded capability checks.

Do not add engine dependencies to the primary library. Keep transport-specific
implementations separate from the engine-independent core when a concrete
transport requires them.
