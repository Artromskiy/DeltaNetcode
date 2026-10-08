# DeltaNetcode workflow

## Repository layout

This repository follows the shared first-party layout. Before restore/build or
a structural handoff, run:

```bash
./eng/check-layout.sh
```

The consumer-level NUnit suite is
`tests/DeltaNetcode.Consumer.Tests/DeltaNetcode.Consumer.Tests.csproj`. It uses
the Maze sample to cover local and client/server command flows, prediction and
rollback, multiple clients, malformed messages, snapshots, join/resume and
journal replay, including cancellation tombstones.

## Correctness and build

```bash
dotnet restore DeltaNetcode.slnx -p:NuGetAudit=false
dotnet build DeltaNetcode.slnx -c Release --no-restore \
  --disable-build-servers -m:1 /p:UseSharedCompilation=false -v:minimal
dotnet test tests/DeltaNetcode.Consumer.Tests/DeltaNetcode.Consumer.Tests.csproj \
  -c Release --no-build --no-restore --disable-build-servers -m:1
git diff --check
```

## Shared pre-commit checks

Before every commit, run the shared formatter and code-metrics analyzer against
this repository:

```bash
../eng/format.sh "$PWD"
FORMAT_CHECK=1 ../eng/format.sh "$PWD"
../eng/code-metrics.sh "$PWD" -v:q
```

Do not run full BenchmarkDotNet measurements unless explicitly requested.

## NuGet

Use the workspace `dev` and `release` modes documented in
../docs/NUGET_WORKFLOW.md. Do not publish from a project-local command.
