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
}
