# DeltaNetcode

DeltaNetcode is an engine independent .NET library for sessions whose game
state belongs to the application. The library provides command identity,
typed registration, command validation and mutation stages, an in-memory
journal, transport framing, session join/resume, routing, and a fixed step rollback model.
Applications provide payload encoding, simulation state serialization,
authentication, transport, and game command handlers.

## Command types

Commands can be non-null structs or classes. Register them explicitly, or mark
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
namespace, nested type names, and generic arity.

## Session model

The application implements `ICommandPayloadHandler`, `ISimulation`, and typed
command handlers. `RollbackSessionModel` owns the fixed step history and calls
each `CommandEntry.Execute()` before the matching simulation tick. Callers own
the clock and invoke `ISession.Tick(step)` for discrete steps. `CurrentStep`
reports the last applied step. `VisitCommandType<TVisitor>` dispatches a
registered ID to a class or struct visitor; `ReadAcceptedAfter(cursor)` reads
accepted replay records without exposing the journal.

`SessionHost.Send<T>` obtains the registered ID, session author and sequence.
In `SessionMode.Local` and `SessionMode.Server`, it runs validation, scheduling
and mutation before adding the final payload to the model. Server sessions
broadcast the authoritative outcome. In `SessionMode.Client`, it sends a
proposal through `ITransport`; commands marked with
`[NetCommand(Predicted = true)]` are also scheduled in the local model. Accepted
server outcomes replace predictions with the authoritative payload; rejected
outcomes remove them and roll back the model. Other client commands wait for
the server outcome before entering local simulation. `SessionServer.Bind`
attaches an authenticated connection to a session and author. Feed messages to
`SessionServer.Receive`; on clients, `SessionClient.Receive` applies server
outcomes and coordinates snapshot or replay synchronization. Validators receive
`CommandValidationContext.CurrentStep` from the authoritative session.

`SessionHost.Send` serializes the payload immediately and retains the encoded
bytes. Mutating a class command after `Send` does not change the queued or
transmitted command.

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

Register validators, mutators and executors independently through `ISession`.
The non-generic `Register` overload attaches a common validator.
Mutators receive a transactional `CommandPreparation` for allocating stable
IDs and seeds; only accepted commands commit its state. Accepted payload bytes
are owned by the journal and reused during rollback.

`CommandPreparationState` seeds the ID allocator and deterministic seed stream.
`ReserveIds(count)` allocates a contiguous range. Pass the first free ID from
the loaded game state when creating the session.

`SessionHost.CaptureSnapshot()` asks the session model for a replayable history
anchor. With `RollbackSessionModel`, this is the oldest retained rollback state;
it can precede the model's current step so that late accepted commands remain
replayable after restoration. `ISessionModel.SaveReplayAnchor` gives custom
models the same integration point for their own history. `Restore` checks the
session and protocol IDs, restores the model and command preparation state, and
clears the old command journal.

The snapshot cursor combines the completed anchor step with the highest
authoritative session order observed at capture time. The journal returns
accepted records after the cursor in `(Step, Order)` order. This includes
future commands accepted before the snapshot and commands accepted later
within the retained rollback window. `SessionClient` applies the snapshot, its
accepted tail and unresolved outcomes before it becomes ready. When the journal
can provide complete revision history, a reconnect replays only changed
outcomes; otherwise it falls back to a fresh snapshot. Pending proposals and
cancellations are sent again after synchronization using their original keys
and payload bytes.

```csharp
SessionSnapshot snapshot = authority.CaptureSnapshot();
var snapshotBytes = new ArrayBufferWriter<byte>();
CommandProtocol.WriteSnapshot(snapshot, snapshotBytes);

// Send the snapshot frame, then replay these records on the restored client.
foreach (JournalRecord record in authority.ReadAcceptedAfter(snapshot.Cursor))
{
    var outcome = new CommandOutcome(record.Result, record.Header);
    client.ApplyOutcome(outcome, record.FinalPayload.Span);
}
```

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
runtime, generator, analyzer and code fix. The [Maze sample](samples/DeltaNetcode.Maze) shows consumer usage.

## Further reading

- [Public API guide](docs/API.md)
