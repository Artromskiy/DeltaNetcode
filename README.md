# DeltaNetcode

DeltaNetcode is an engine independent .NET library for sessions whose game
state belongs to the application. The library provides command identity,
typed registration, command validation and mutation stages, an in-memory
journal, transport framing, session routing, and a fixed step rollback model.
Applications provide payload encoding, simulation state serialization,
authentication, transport, and game command handlers.

## Command types

Commands are value types. Register them explicitly, or mark them with
`[NetCommand]` and use the generated registration list from the companion
source generator. Generation supplies registrations; the application chooses
when and which registrations to add. The analyzer reports an informational
diagnostic for a name-derived ID and its code fix pins that ID into the
attribute.

```csharp
using Delta.Netcode;

[NetCommand(Id = 0x01784B1922C0A132UL)]
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

To register only selected generated commands, pass an individual property such
as `GeneratedCommands.Registration_0x01784B1922C0A132` to `Register`.

An explicit ID is stable across type renames. ID `0` is reserved. The
name-derived ID uses UTF-8 FNV-1a 64 over the CLR metadata full name, including
namespace, nested type names, and generic arity.

## Session model

The application implements `ICommandPayloadHandler`, `ISimulation`, and typed
command handlers. `RollbackSessionModel` owns the fixed step history and calls
each `CommandEntry.Execute()` before the matching simulation tick. Callers own
the clock and invoke `ISession.Tick(step)` for discrete steps.

`SessionHost.Send<T>` obtains the registered ID, assigns the session author and
sequence, stores the command, updates the local model, and sends a proposal
through the configured `ITransport`. `SessionServer.Bind` attaches an
authenticated connection to a session and author. Feed transport messages to
`SessionServer.Receive`; route responses accepted by a client through
`CommandProtocol.TryReadOutcome` and `SessionHost.ApplyOutcome`.

```csharp
var model = new RollbackSessionModel(simulation, initialStep: 0, historyDepth: 120);
var session = new SessionHost(
    new SessionStart(sessionId, authorId, protocolId, Step: 0, Seed: seed),
    commands,
    payloadHandler,
    model,
    new MemoryCommandJournal(),
    transport: transport);

session.Register<MoveCommand>(
    moveCommandId,
    validator: movementValidator,
    mutator: movementMutator,
    executor: movementExecutor);

CommandKey key = session.Send(new MoveCommand { EntityId = 7, X = 1, Y = 0 }, step: 12);
session.Tick(12);
```

Each registration can supply a validator, mutator, and executor independently.
Common validators can be attached with `SessionHost.AddValidator`. Mutators
receive a transactional `CommandPreparation` for allocating stable IDs and
seeds; only accepted commands commit its state. Accepted payload bytes are
owned by the journal and reused during rollback.

## Transport contract

`CommandProtocol` encodes proposals, cancellations, and outcomes as byte
messages. It does not choose sockets, reliability, encryption, authentication,
or packet batching. The application owns those choices through `ITransport`.
The server trusts author identity only from the connection binding; the author
field inside an incoming message is replaced before processing.

The built-in `MemoryCommandJournal` retains command outcomes and cancellation
tombstones for the lifetime of the session. Session state persistence beyond
the simulation/model `Save` and `Load` contracts is not provided yet.

## Projects

- `src/DeltaNetcode` — runtime contracts and implementation.
- `src/DeltaNetcode.Generators` — command catalog generator and ID analyzer.
- `src/DeltaNetcode.CodeFixes` — analyzer code fix for pinning IDs.
- `WORKFLOW.md` — build and repository checks.
