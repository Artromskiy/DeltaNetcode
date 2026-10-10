# DeltaNetcode

DeltaNetcode is an engine independent .NET library for sessions whose game
state belongs to the application. The library provides command identity,
typed registration, command validation and mutation stages, an in-memory
journal, transport framing, session join/resume, routing, and a fixed-step
rollback model.
Applications provide payload encoding, simulation state serialization,
authentication, transport, and game command handlers.

## What it provides

- Stable command IDs with generated registrations, an analyzer and an ID code fix.
- Typed validation, authoritative command mutation and fixed-step execution.
- Optional client prediction with rollback when the server changes an outcome.
- A memory journal, snapshots, and join/resume synchronization by replay or snapshot.
- Transport-independent session routing and optional transient simulation updates.

## Command types

Command payloads can be value or reference types; the application codec defines
their byte encoding and null handling. Register them explicitly, or mark
them with `[NetCommand]` and use the generated registration list from the
companion source generator. Generation supplies registrations; the application chooses
when and which registrations to add. The analyzer reports an informational
diagnostic for a name-derived ID and its code fix pins that ID into the
attribute.

```csharp
using Delta.Netcode;

[NetCommand(Id = 0x01784B1922C0A132UL, Predicted = true)]
public struct MoveCommand
{
    public int EntityId;
    public float X;
    public float Y;
}

var commands = new CommandRegistry();
foreach (ICommandRegistration registration in GeneratedCommands.Registrations)
{
    commands.Register(registration);
}
```

To register one generated command, pass `GeneratedCommands.GetRegistration<MoveCommand>()`
to `Register`. Read its ID from the registry with `commands.GetId<MoveCommand>()`.

An explicit ID is stable across type renames. ID `0` is reserved. The
name-derived ID uses UTF-8 FNV-1a 64 over the CLR metadata full name, including
namespace, nested type names (joined with `+`) and generic arity.

## Session model

The application implements `ICommandPayloadHandler`, `ISimulation` and typed
command handlers. `RollbackSessionModel` stores bounded history, executes
commands in authoritative order, then advances the simulation. The application
owns the clock and calls `ISession.Tick(step)` for each fixed step.

`SessionMode.Local` processes commands in the local host; `Server` validates
proposals and broadcasts accepted outcomes when configured with a transport;
`Client` submits proposals and may predict commands marked
`[NetCommand(Predicted = true)]`. `SessionClient` synchronizes a client through
snapshot/replay join and resume. The server trusts authors bound to authenticated
connections. Validators receive the authoritative
`CommandValidationContext.CurrentStep`.

`SessionHost.Send` serializes the payload immediately and retains the encoded
bytes. Mutating a class command after `Send` does not change the queued or
transmitted command.

For optional higher-frequency local updates, implement
`ITransientSimulation<TInput>` alongside `ISimulation`. The application frame
loop calls `TickTransient` to run only the simulation portion between fixed
session ticks. After each fixed `ISession.Tick`, save the simulation state with
`ISimulation.Save`; before the next fixed tick, restore it with `ISimulation.Load`,
send that step's input, and call `ISession.Tick`. Transient updates do not advance
the session step or enter the command journal. The application owns the frame
clock and the fixed-state checkpoint.

```csharp
var model = new RollbackSessionModel(simulation, initialStep: 0, historyDepth: 120);
var session = new SessionHost(
    new SessionStart(sessionId, authorId, protocolId, Step: 0, Seed: seed),
    commands,
    payloadHandler,
    model,
    new MemoryCommandJournal(),
    transport: transport,
    mode: SessionMode.Client);

session.Register<MoveCommand>(moveCommandId);
session.Register<MoveCommand>(movementValidator);
session.Register<MoveCommand>(movementMutator);
session.Register<MoveCommand>(movementExecutor);

var client = new SessionClient(session);
client.BeginJoin(transport, connectionId);
// Feed server frames to client.Receive(message) and wait until client.IsReady.

// Submit commands after synchronization completes.
CommandKey key = session.Send(new MoveCommand { EntityId = 7, X = 1, Y = 0 }, step: 12);
session.Tick(12);
```

Registration, deterministic preparation state, snapshots and replay are
described in the [API guide](docs/API.md).

## Transport contract

`CommandProtocol` encodes proposals, cancellations, outcomes, snapshots and
join/resume control messages as byte frames. It does not choose sockets,
reliability, encryption, authentication, or packet batching. The application
owns those choices through `ITransport`; synchronization responses must be
delivered in order. The application authenticates a connection and calls
`SessionServer.Bind` before it can request synchronization. The server trusts
author identity only from that binding; the author field inside a command
message is replaced before processing.

The built-in `MemoryCommandJournal` retains command outcomes and cancellation
tombstones for the lifetime of the session and exposes revisioned changes for
incremental resume. A journal without revision history uses snapshot sync.
`SessionSnapshot` captures model state and command preparation state;
applications choose where to persist or how to transport it. `CommandProtocol`
frames snapshots but does not compress or fragment them.

## Packages and examples

The DeltaNetcode NuGet package targets .NET Standard 2.1 and .NET 10 with its
runtime, generator, analyzer and code fix. The [Maze sample](samples/DeltaNetcode.Maze)
shows game commands, while the [generated-command sample](samples/DeltaNetcode.Aot)
shows generated registration and typed command dispatch.

```xml
<PackageReference Include="DeltaNetcode" Version="0.0.4" />
```

## Further reading

- [Public API guide](docs/API.md)
- [Wire protocol](docs/PROTOCOL.md)
- [Transient simulation updates](docs/TRANSIENT-SIMULATION.md)
