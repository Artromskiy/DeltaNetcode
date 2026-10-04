using System.Buffers;
using System.Buffers.Binary;
using Delta.Netcode;
using DeltaNetcode.Maze;
using NUnit.Framework;

namespace DeltaNetcode.Consumer.Tests;

[TestFixture]
public sealed class MazeConsumerTests
{
    private static readonly SessionId SessionId = new(42);
    private static readonly ProtocolId ProtocolId = new(1);

    [Test]
    public void GeneratedRegistrationCanBeSelectedByCommandType()
    {
        ICommandRegistration registration = GeneratedCommands.GetRegistration<MoveCommand>();

        Assert.That(registration.Id, Is.EqualTo(MoveCommand.Id));
        Assert.That(registration.IsPredicted, Is.True);
    }

    [Test]
    public void CommandRegistrationAlsoAcceptsReferencePayloadTypes()
    {
        var registry = new CommandRegistry();
        registry.Register<ReferencePayload>(0x4D415A4500000002);

        Assert.That(registry.GetId<ReferencePayload>(), Is.EqualTo(0x4D415A4500000002));
        Assert.That(new Command<ReferencePayload>(default, new ReferencePayload()).Payload, Is.TypeOf<ReferencePayload>());
    }

    [Test]
    public void LocalSessionAppliesMoveAndCollectsLastCoin()
    {
        MazeWorld world = CreateWorld((1, 1), (2, 1));
        SessionHost session = MazeSession.Create(SessionId, ProtocolId, new AuthorId(1), world);

        session.Send(new MoveCommand(MoveDirection.Right), 0);
        session.Tick(0);

        Assert.That(world.Players[1].X, Is.EqualTo(2));
        Assert.That(world.Players[1].Score, Is.EqualTo(1));
        Assert.That(world.IsFinished, Is.True);
        Assert.That(world.Winner, Is.EqualTo(1));
    }

    [Test]
    public void ClientServerSessionPredictsThenReconcilesAcceptedAndRejectedCommands()
    {
        MazeWorld serverWorld = CreateWorld((998, 1), (2, 1), coinAt: (999, 1));
        var serverOutbound = new ClientFanoutTransport();
        var server = new SessionServer(serverOutbound);
        SessionHost authority = MazeSession.Create(SessionId, ProtocolId, new AuthorId(0), serverWorld, mode: SessionMode.Server);
        server.Add(authority);

        MazeWorld clientWorld = CreateWorld((998, 1), (2, 1), coinAt: (999, 1));
        const ulong connection = 10;
        SessionHost client = MazeSession.Create(
            SessionId,
            ProtocolId,
            new AuthorId(1),
            clientWorld,
            new DelegateTransport((_, message) => server.Receive(connection, message)),
            mode: SessionMode.Client);
        client.AttachConnection(connection);
        server.Bind(connection, authority, new AuthorId(1));
        serverOutbound.Register(connection, message => ApplyOutcome(client, message));

        client.Send(new MoveCommand(MoveDirection.Right), 0);
        client.Tick(0);
        authority.Tick(0);

        Assert.That(clientWorld.Players[1].X, Is.EqualTo(999));
        Assert.That(serverWorld.Players[1].X, Is.EqualTo(999));
        Assert.That(clientWorld.Players[1].Score, Is.EqualTo(1));
        Assert.That(serverWorld.IsFinished, Is.True);

        client.Send(new MoveCommand(MoveDirection.Right), 1);
        client.Tick(1);
        authority.Tick(1);

        Assert.That(clientWorld.Players[1].X, Is.EqualTo(999));
        Assert.That(serverWorld.Players[1].X, Is.EqualTo(999));
    }

    [Test]
    public void MultipleClientsShareServerAndReconcileOrderedCommands()
    {
        MazeWorld serverWorld = CreateWorld((1, 1), (3, 1), coinAt: (2, 1));
        var outbound = new ClientFanoutTransport();
        var server = new SessionServer(outbound);
        SessionHost authority = MazeSession.Create(SessionId, ProtocolId, new AuthorId(0), serverWorld, mode: SessionMode.Server);
        server.Add(authority);

        MazeWorld firstWorld = CreateWorld((1, 1), (3, 1), coinAt: (2, 1));
        MazeWorld secondWorld = CreateWorld((1, 1), (3, 1), coinAt: (2, 1));
        SessionHost first = CreateClient(server, outbound, authority, firstWorld, 11, new AuthorId(1));
        SessionHost second = CreateClient(server, outbound, authority, secondWorld, 12, new AuthorId(2));

        first.Send(new MoveCommand(MoveDirection.Right), 0);
        second.Send(new MoveCommand(MoveDirection.Left), 0);
        first.Tick(0);
        second.Tick(0);
        authority.Tick(0);

        Assert.That(serverWorld.Players[1].X, Is.EqualTo(2));
        Assert.That(serverWorld.Players[2].X, Is.EqualTo(2));
        Assert.That(serverWorld.Players[1].Score + serverWorld.Players[2].Score, Is.EqualTo(1));
        AssertWorldMatches(firstWorld, serverWorld);
        AssertWorldMatches(secondWorld, serverWorld);
    }

    [Test]
    public void LateAuthoritativeOutcomeRollsBackPredictionAndReplaysAcceptedCommands()
    {
        MazeWorld world = CreateWorld((1, 1), (4, 1), coinAt: (3, 1));
        SessionHost client = MazeSession.Create(SessionId, ProtocolId, new AuthorId(1), world, mode: SessionMode.Client);
        CommandKey predicted = client.Send(new MoveCommand(MoveDirection.Right), 0);
        client.Tick(0);
        client.Tick(1);

        var authoritativeHeader = new CommandHeader(predicted, MoveCommand.Id, 0, 0);
        client.ApplyOutcome(new CommandOutcome(CommandResult.Accepted, authoritativeHeader), [.. MazePayloadCodec.Encode(new MoveCommand(MoveDirection.Left))]);
        client.Tick(1);

        Assert.That(world.Players[1].X, Is.Zero);
        Assert.That(world.Players[1].Score, Is.Zero);
    }

    [Test]
    public void RejectedPredictedCommandRollsBackClientWorld()
    {
        MazeWorld world = CreateWorld((1, 1), (4, 1), coinAt: (3, 1));
        SessionHost client = MazeSession.Create(SessionId, ProtocolId, new AuthorId(1), world, mode: SessionMode.Client);
        CommandKey key = client.Send(new MoveCommand(MoveDirection.Right), 0);
        client.Tick(0);
        Assert.That(world.Players[1].X, Is.EqualTo(2));

        var rejectedHeader = new CommandHeader(key, MoveCommand.Id, 0, 0);
        client.ApplyOutcome(new CommandOutcome(CommandResult.Rejected, rejectedHeader), []);
        client.Tick(0);

        Assert.That(world.Players[1].X, Is.EqualTo(1));
        Assert.That(world.Players[1].Score, Is.Zero);
    }

    [Test]
    public void InvalidProtocolAndMoveAreReportedWithoutChangingWorld()
    {
        MazeWorld world = CreateWorld((1, 1), (3, 1));
        SessionHost authority = MazeSession.Create(SessionId, ProtocolId, new AuthorId(0), world, mode: SessionMode.Server);
        var server = new SessionServer();
        server.Add(authority);
        server.Bind(20, authority, new AuthorId(1));

        Assert.That(server.Receive(20, [1, 2, 3]), Is.EqualTo(CommandResult.InvalidMessage));

        byte[] proposal = CommandProtocol.EncodeProposal(
            new CommandHeader(new CommandKey(SessionId, new AuthorId(99), 4), MoveCommand.Id, 0, 0),
            MazePayloadCodec.Encode(new MoveCommand((MoveDirection)255)));
        Assert.That(server.Receive(20, proposal), Is.EqualTo(CommandResult.Rejected));
        Assert.That(world.Players[1].X, Is.EqualTo(1));
    }

    [Test]
    public void MalformedTypedPayloadIsReportedAsInvalidMessage()
    {
        MazeWorld world = CreateWorld((1, 1), (3, 1));
        SessionHost authority = MazeSession.Create(SessionId, ProtocolId, new AuthorId(0), world, mode: SessionMode.Server);
        var server = new SessionServer();
        server.Add(authority);
        server.Bind(20, authority, new AuthorId(1));
        byte[] proposal = CommandProtocol.EncodeProposal(
            new CommandHeader(new CommandKey(SessionId, new AuthorId(9), 1), MoveCommand.Id, 0, 0),
            [1, 2]);

        Assert.That(server.Receive(20, proposal), Is.EqualTo(CommandResult.InvalidMessage));
        Assert.That(world.Players[1].X, Is.EqualTo(1));
    }

    [Test]
    public void UnboundClientCanReconnectAndResumeSubmittingCommands()
    {
        MazeWorld world = CreateWorld((1, 1), (3, 1));
        SessionHost authority = MazeSession.Create(SessionId, ProtocolId, new AuthorId(0), world, mode: SessionMode.Server);
        var outbound = new ClientFanoutTransport();
        var server = new SessionServer(outbound);
        server.Add(authority);
        const ulong oldConnection = 30;
        const ulong newConnection = 31;
        Assert.That(server.Unbind(oldConnection), Is.False);
        server.Bind(oldConnection, authority, new AuthorId(1));
        Assert.That(server.Unbind(oldConnection), Is.True);
        outbound.Register(newConnection, _ => { });
        server.Bind(newConnection, authority, new AuthorId(1));
        byte[] proposal = CommandProtocol.EncodeProposal(
            new CommandHeader(new CommandKey(SessionId, new AuthorId(1), 0), MoveCommand.Id, 0, 0),
            MazePayloadCodec.Encode(new MoveCommand(MoveDirection.Right)));

        Assert.That(server.Receive(oldConnection, proposal), Is.EqualTo(CommandResult.Rejected));
        Assert.That(server.Receive(newConnection, proposal), Is.EqualTo(CommandResult.Accepted));
    }

    [Test]
    public void LocalSendValidatesMutatesAndExecutesThroughOnePath()
    {
        (SessionHost session, TestSimulation simulation, MemoryCommandJournal journal) = CreateTestSession(
            SessionMode.Local,
            new AuthorId(1),
            new CommandPreparationState(100, 123));
        session.Register<TestCommand>(new TestCommandValidator(allow: true));

        CommandKey key = session.Send(new TestCommand(5, 0, 0), 0);
        Assert.That(journal.TryGet(key, out JournalRecord? record), Is.True);
        Assert.That(record!.Result, Is.EqualTo(CommandResult.Accepted));
        Assert.That(TestCommandCodec.Decode(record.FinalPayload.Span), Is.EqualTo(new TestCommand(5, 100, TestSeed(123))));

        session.Tick(0);

        Assert.That(simulation.Value, Is.EqualTo(5));
        Assert.That(simulation.ExecutionCount, Is.EqualTo(1));
        Assert.That(simulation.LastId, Is.EqualTo(100));

        session.Register<TestCommand>(new TestCommandValidator(allow: false));
        CommandKey rejectedKey = session.Send(new TestCommand(7, 0, 0), 1);

        Assert.That(journal.TryGet(rejectedKey, out JournalRecord? rejected), Is.True);
        Assert.That(rejected!.Result, Is.EqualTo(CommandResult.Rejected));
        Assert.That(session.CaptureSnapshot().Preparation.NextId, Is.EqualTo(104));
        session.Tick(1);
        Assert.That(simulation.Value, Is.EqualTo(5));
    }

    [Test]
    public void ServerSendUsesAuthoritativePipelineAndBroadcastsOutcome()
    {
        var transport = new ClientFanoutTransport();
        var server = new SessionServer(transport);
        (SessionHost authority, TestSimulation serverSimulation, _) = CreateTestSession(
            SessionMode.Server,
            new AuthorId(1),
            new CommandPreparationState(500, 17));
        (SessionHost client, TestSimulation clientSimulation, _) = CreateTestSession(
            SessionMode.Client,
            new AuthorId(2));
        const ulong connection = 50;
        server.Add(authority);
        server.Bind(connection, authority, new AuthorId(2));
        transport.Register(connection, message => ApplyOutcome(client, message));

        authority.Send(new TestCommand(9, 0, 0), 0);
        authority.Tick(0);
        client.Tick(0);

        Assert.That(serverSimulation.Value, Is.EqualTo(9));
        Assert.That(clientSimulation.Value, Is.EqualTo(9));
        Assert.That(clientSimulation.LastId, Is.EqualTo(500));
        Assert.That(serverSimulation.LastId, Is.EqualTo(500));
    }

    [Test]
    public void SnapshotRoundTripsOwnedModelStateAndPreparation()
    {
        byte[] source = [1, 2, 3, 4];
        var snapshot = new SessionSnapshot(
            SessionId,
            ProtocolId,
            12,
            new CommandCursor(12, 23),
            new CommandPreparationState(80, 90),
            source);
        source[0] = byte.MaxValue;

        var buffer = new ArrayBufferWriter<byte>();
        CommandProtocol.WriteSnapshot(snapshot, buffer);
        bool decoded = CommandProtocol.TryReadSnapshot(buffer.WrittenSpan, out SessionSnapshot? copy);

        Assert.That(decoded, Is.True);
        Assert.That(copy, Is.Not.Null);
        Assert.That(copy!.SessionId, Is.EqualTo(SessionId));
        Assert.That(copy.ProtocolId, Is.EqualTo(ProtocolId));
        Assert.That(copy.Step, Is.EqualTo(12));
        Assert.That(copy.Cursor, Is.EqualTo(new CommandCursor(12, 23)));
        Assert.That(copy.Preparation, Is.EqualTo(new CommandPreparationState(80, 90)));
        Assert.That(copy.ModelState.ToArray(), Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
        Assert.That(CommandProtocol.TryReadSnapshot(buffer.WrittenSpan[..^1], out _), Is.False);
    }

    [Test]
    public void JournalReturnsFutureAndPostSnapshotCommandsInSimulationOrder()
    {
        var journal = new MemoryCommandJournal();
        JournalRecord old = CreateRecord(sequence: 1, step: 0, order: 2);
        JournalRecord future = CreateRecord(sequence: 2, step: 4, order: 2);
        JournalRecord late = CreateRecord(sequence: 3, step: 0, order: 4);
        JournalRecord cancelled = CreateRecord(sequence: 4, step: 5, order: 3);
        journal.Append(old);
        journal.Append(future);
        journal.Append(late);
        journal.Append(cancelled);
        journal.Cancel(cancelled.Header.Key);

        JournalRecord[] records = journal.ReadAcceptedAfter(new CommandCursor(0, 3)).ToArray();

        Assert.That(records.Select(record => record.Header.Key.Sequence), Is.EqualTo(new ulong[] { 3, 2 }));
        journal.Clear();
        Assert.That(journal.TryGet(old.Header.Key, out _), Is.False);
    }

    [Test]
    public void LateJoinerRestoresSnapshotAndAppliesCommandsAcceptedDuringTransfer()
    {
        var journal = new MemoryCommandJournal();
        (SessionHost authority, TestSimulation serverSimulation, _) = CreateTestSession(
            SessionMode.Server,
            new AuthorId(1),
            new CommandPreparationState(100, 123),
            journal);
        (SessionHost client, TestSimulation clientSimulation, _) = CreateTestSession(
            SessionMode.Client,
            new AuthorId(2));

        authority.Send(new TestCommand(1, 0, 0), 0);
        authority.Tick(0);
        authority.Send(new TestCommand(10, 0, 0), 2);
        SessionSnapshot snapshot = authority.CaptureSnapshot();

        client.Restore(snapshot);
        Assert.That(clientSimulation.Value, Is.Zero);

        var lateProposal = new CommandHeader(
            new CommandKey(SessionId, new AuthorId(999), 0),
            TestCommandId,
            0,
            1);
        CommandOutcome accepted = authority.Receive(new AuthorId(2), lateProposal, TestCommandCodec.Encode(new TestCommand(100, 0, 0)));
        Assert.That(accepted.Result, Is.EqualTo(CommandResult.Accepted));
        authority.Tick(0);
        authority.Tick(2);

        JournalRecord[] catchUp = journal.ReadAcceptedAfter(snapshot.Cursor).ToArray();
        Assert.That(catchUp.Select(record => record.Header.Step), Is.EqualTo(new long[] { 0, 0, 2 }));
        foreach (JournalRecord record in catchUp)
        {
            var outcome = new CommandOutcome(record.Result, record.Header);
            client.ApplyOutcome(outcome, record.FinalPayload.Span);
            client.ApplyOutcome(outcome, record.FinalPayload.Span);
        }

        client.Tick(0);
        client.Tick(2);

        Assert.That(clientSimulation.Value, Is.EqualTo(serverSimulation.Value));
        Assert.That(clientSimulation.ExecutionCount, Is.EqualTo(serverSimulation.ExecutionCount));
        Assert.That(clientSimulation.LastId, Is.EqualTo(serverSimulation.LastId));
    }

    [Test]
    public void RepeatedAuthoritativeOutcomeDoesNotExecuteAgain()
    {
        (SessionHost client, TestSimulation simulation, _) = CreateTestSession(SessionMode.Client, new AuthorId(1));
        var key = new CommandKey(SessionId, new AuthorId(2), 0);
        var header = new CommandHeader(key, TestCommandId, 0, 1);
        var outcome = new CommandOutcome(CommandResult.Accepted, header);
        byte[] payload = TestCommandCodec.Encode(new TestCommand(3, 9, 11));

        client.ApplyOutcome(outcome, payload);
        client.Tick(0);
        client.ApplyOutcome(outcome, payload);
        client.Tick(0);

        Assert.That(simulation.Value, Is.EqualTo(3));
        Assert.That(simulation.ExecutionCount, Is.EqualTo(1));
    }

    [Test]
    public void OutcomeAlreadyIncludedInSnapshotIsIgnored()
    {
        var journal = new MemoryCommandJournal();
        (SessionHost authority, _, _) = CreateTestSession(SessionMode.Server, new AuthorId(1), journal: journal);
        (SessionHost client, TestSimulation clientSimulation, _) = CreateTestSession(SessionMode.Client, new AuthorId(2));

        CommandKey key = authority.Send(new TestCommand(4, 0, 0), 0);
        authority.Tick(0);
        for (long step = 1; step <= 8; step++)
        {
            authority.Tick(step);
        }

        SessionSnapshot snapshot = authority.CaptureSnapshot();
        Assert.That(snapshot.Step, Is.EqualTo(0));
        client.Restore(snapshot);
        Assert.That(journal.TryGet(key, out JournalRecord? record), Is.True);

        client.ApplyOutcome(new CommandOutcome(record!.Result, record.Header), record.FinalPayload.Span);
        client.Tick(snapshot.Step);

        Assert.That(clientSimulation.Value, Is.EqualTo(4));
        Assert.That(clientSimulation.ExecutionCount, Is.EqualTo(1));
    }

    [Test]
    public void RestoringSnapshotRestoresPreparationStateAndChecksSessionIdentity()
    {
        (SessionHost session, _, MemoryCommandJournal journal) = CreateTestSession(
            SessionMode.Local,
            new AuthorId(1),
            new CommandPreparationState(200, 300));
        SessionSnapshot initial = session.CaptureSnapshot();
        CommandKey firstKey = session.Send(new TestCommand(1, 0, 0), 0);
        Assert.That(journal.TryGet(firstKey, out JournalRecord? first), Is.True);
        TestCommand firstCommand = TestCommandCodec.Decode(first!.FinalPayload.Span);

        session.Restore(initial);
        Assert.That(journal.TryGet(firstKey, out _), Is.False);
        CommandKey secondKey = session.Send(new TestCommand(1, 0, 0), 0);
        Assert.That(journal.TryGet(secondKey, out JournalRecord? second), Is.True);
        Assert.That(TestCommandCodec.Decode(second!.FinalPayload.Span), Is.EqualTo(firstCommand));

        var wrongSession = new SessionSnapshot(new SessionId(999), ProtocolId, initial.Step, initial.Cursor, initial.Preparation, initial.ModelState.Span);
        Assert.Throws<ArgumentException>(() => session.Restore(wrongSession));
        var wrongProtocol = new SessionSnapshot(SessionId, new ProtocolId(999), initial.Step, initial.Cursor, initial.Preparation, initial.ModelState.Span);
        Assert.Throws<ArgumentException>(() => session.Restore(wrongProtocol));
    }

    private static SessionHost CreateClient(
        SessionServer server,
        ClientFanoutTransport outbound,
        SessionHost authority,
        MazeWorld world,
        ulong connection,
        AuthorId author)
    {
        SessionHost client = MazeSession.Create(
            SessionId,
            ProtocolId,
            author,
            world,
            new DelegateTransport((_, message) => server.Receive(connection, message)),
            mode: SessionMode.Client);
        client.AttachConnection(connection);
        server.Bind(connection, authority, author);
        outbound.Register(connection, message => ApplyOutcome(client, message));
        return client;
    }

    private static MazeWorld CreateWorld((int X, int Y) playerOne, (int X, int Y) playerTwo, (int X, int Y)? coinAt = null)
    {
        var world = new MazeWorld(1000, 1000);
        world.AddPlayer(new PlayerState(1, playerOne.X, playerOne.Y));
        world.AddPlayer(new PlayerState(2, playerTwo.X, playerTwo.Y));
        world.AddItem(new ItemState(100, coinAt?.X ?? 2, coinAt?.Y ?? 1, ItemType.Coin));
        world.AddItem(new ItemState(101, 900, 900, ItemType.Trap));
        return world;
    }

    private static void AssertWorldMatches(MazeWorld actual, MazeWorld expected)
    {
        foreach ((int id, PlayerState player) in expected.Players)
        {
            Assert.That(actual.Players[id], Is.EqualTo(player));
        }

        Assert.That(actual.Items.OrderBy(item => item.Key), Is.EqualTo(expected.Items.OrderBy(item => item.Key)));
    }

    private static void ApplyOutcome(SessionHost client, byte[] message)
    {
        if (!CommandProtocol.TryReadOutcome(message, out CommandOutcome outcome, out ReadOnlySpan<byte> finalPayload))
        {
            throw new InvalidOperationException("Server sent an invalid outcome frame.");
        }

        client.ApplyOutcome(outcome, finalPayload);
    }

    private static (SessionHost Session, TestSimulation Simulation, MemoryCommandJournal Journal) CreateTestSession(
        SessionMode mode,
        AuthorId author,
        CommandPreparationState? preparation = null,
        MemoryCommandJournal? journal = null)
    {
        var registry = new CommandRegistry();
        registry.Register<TestCommand>(TestCommandId);
        var simulation = new TestSimulation();
        journal ??= new MemoryCommandJournal();
        var session = new SessionHost(
            new SessionStart(SessionId, author, ProtocolId, Step: 0, Seed: 123),
            registry,
            new TestCommandPayloadHandler(),
            new RollbackSessionModel(simulation, initialStep: 0, historyDepth: 8),
            journal,
            mode: mode,
            preparationState: preparation);
        session.Register<TestCommand>(new TestCommandMutator());
        session.Register<TestCommand>(new TestCommandExecutor(simulation));
        return (session, simulation, journal);
    }

    private static JournalRecord CreateRecord(ulong sequence, long step, uint order)
    {
        var key = new CommandKey(SessionId, new AuthorId(1), sequence);
        var header = new CommandHeader(key, TestCommandId, step, order);
        byte[] payload = TestCommandCodec.Encode(new TestCommand(1, sequence, sequence));
        return new JournalRecord(header, CommandResult.Accepted, payload, payload);
    }

    private static ulong TestSeed(ulong initialState)
    {
        ulong value = initialState + 0x9E3779B97F4A7C15UL;
        value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
        return value ^ (value >> 31);
    }

    private sealed class DelegateTransport(Action<ulong, byte[]> send) : ITransport
    {
        public void Send(ulong connectionId, ReadOnlySpan<byte> message) => send(connectionId, message.ToArray());
    }

    private sealed class ClientFanoutTransport : ITransport
    {
        private readonly Dictionary<ulong, Action<byte[]>> _clients = [];

        public void Register(ulong connectionId, Action<byte[]> receive) => _clients.Add(connectionId, receive);

        public void Send(ulong connectionId, ReadOnlySpan<byte> message)
            => _clients[connectionId](message.ToArray());
    }

    private sealed class ReferencePayload
    {
    }

    private const ulong TestCommandId = 0x4D415A4500000003;

    private readonly record struct TestCommand(int Value, ulong Id, ulong Seed);

    private sealed class TestSimulation : ISimulation
    {
        public int Value { get; set; }

        public int ExecutionCount { get; set; }

        public ulong LastId { get; set; }

        public ulong LastSeed { get; set; }

        public void Tick(long simulationStep)
        {
        }

        public void Save(IBufferWriter<byte> output)
        {
            Span<byte> state = output.GetSpan(28);
            BinaryPrimitives.WriteInt32LittleEndian(state, Value);
            BinaryPrimitives.WriteInt32LittleEndian(state[4..], ExecutionCount);
            BinaryPrimitives.WriteUInt64LittleEndian(state[8..], LastId);
            BinaryPrimitives.WriteUInt64LittleEndian(state[16..], LastSeed);
            BinaryPrimitives.WriteInt32LittleEndian(state[24..], 0);
            output.Advance(28);
        }

        public void Load(ReadOnlySpan<byte> state)
        {
            if (state.Length != 28)
            {
                throw new ArgumentException("Test simulation state must contain 28 bytes.", nameof(state));
            }

            Value = BinaryPrimitives.ReadInt32LittleEndian(state);
            ExecutionCount = BinaryPrimitives.ReadInt32LittleEndian(state[4..]);
            LastId = BinaryPrimitives.ReadUInt64LittleEndian(state[8..]);
            LastSeed = BinaryPrimitives.ReadUInt64LittleEndian(state[16..]);
        }
    }

    private sealed class TestCommandPayloadHandler : ICommandPayloadHandler
    {
        public void Write<T>(in T payload, IBufferWriter<byte> output)
        {
            if (typeof(T) != typeof(TestCommand))
            {
                throw new NotSupportedException($"No test payload codec for {typeof(T)}.");
            }

            Span<byte> destination = output.GetSpan(20);
            TestCommand command = (TestCommand)(object)payload!;
            BinaryPrimitives.WriteInt32LittleEndian(destination, command.Value);
            BinaryPrimitives.WriteUInt64LittleEndian(destination[4..], command.Id);
            BinaryPrimitives.WriteUInt64LittleEndian(destination[12..], command.Seed);
            output.Advance(20);
        }

        public T Read<T>(ReadOnlySpan<byte> payload)
        {
            if (typeof(T) != typeof(TestCommand) || !TestCommandCodec.TryDecode(payload, out TestCommand command))
            {
                throw new ArgumentException("Test command payload must contain 20 bytes.", nameof(payload));
            }

            return (T)(object)command;
        }
    }

    private static class TestCommandCodec
    {
        public static byte[] Encode(TestCommand command)
        {
            byte[] bytes = new byte[20];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, command.Value);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(4), command.Id);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(12), command.Seed);
            return bytes;
        }

        public static TestCommand Decode(ReadOnlySpan<byte> bytes)
        {
            if (!TryDecode(bytes, out TestCommand command))
            {
                throw new ArgumentException("Test command payload must contain 20 bytes.", nameof(bytes));
            }

            return command;
        }

        public static bool TryDecode(ReadOnlySpan<byte> bytes, out TestCommand command)
        {
            if (bytes.Length != 20)
            {
                command = default;
                return false;
            }

            command = new TestCommand(
                BinaryPrimitives.ReadInt32LittleEndian(bytes),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes[4..]),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes[12..]));
            return true;
        }
    }

    private sealed class TestCommandValidator(bool allow) : ICommandValidator<TestCommand>
    {
        public bool Validate(in Command<TestCommand> command) => allow;
    }

    private sealed class TestCommandMutator : ICommandMutator<TestCommand>
    {
        public void Mutate(ref TestCommand payload, CommandPreparation preparation)
        {
            ulong firstId = preparation.ReserveIds(4);
            payload = payload with { Id = firstId, Seed = preparation.NextSeed() };
        }
    }

    private sealed class TestCommandExecutor(TestSimulation simulation) : ICommandExecutor<TestCommand>
    {
        public void Execute(in Command<TestCommand> command)
        {
            simulation.Value += command.Payload.Value;
            simulation.ExecutionCount++;
            simulation.LastId = command.Payload.Id;
            simulation.LastSeed = command.Payload.Seed;
        }
    }
}
