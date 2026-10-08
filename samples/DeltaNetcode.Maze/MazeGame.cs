using System.Buffers;
using System.Buffers.Binary;
using Delta.Netcode;

namespace DeltaNetcode.Maze;

public enum MoveDirection : byte
{
    Up,
    Right,
    Down,
    Left
}

public enum ItemType : byte
{
    Coin,
    Trap
}

[NetCommand(Id = 0x4D415A4500000001, Predicted = true)]
public readonly record struct MoveCommand(MoveDirection Direction)
{
    public const ulong Id = 0x4D415A4500000001;
}

public readonly record struct PlayerState(int Id, int X, int Y, int Score = 0, int Health = 3);

public readonly record struct ItemState(int Id, int X, int Y, ItemType Type);

public sealed class MazeWorld(int width, int height)
{
    public int Width { get; } = width > 0 ? width : throw new ArgumentOutOfRangeException(nameof(width));
    public int Height { get; } = height > 0 ? height : throw new ArgumentOutOfRangeException(nameof(height));
    public Dictionary<int, PlayerState> Players { get; } = [];
    public Dictionary<int, ItemState> Items { get; } = [];
    public bool IsFinished => Items.Values.All(item => item.Type != ItemType.Coin);
    public int Winner => Players.Values.OrderByDescending(player => player.Score).ThenBy(player => player.Id).First().Id;

    public void AddPlayer(PlayerState player)
    {
        if (player.X < 0 || player.X >= Width || player.Y < 0 || player.Y >= Height)
        {
            throw new ArgumentOutOfRangeException(nameof(player));
        }

        Players.Add(player.Id, player);
    }

    public void AddItem(ItemState item)
    {
        if (item.X < 0 || item.X >= Width || item.Y < 0 || item.Y >= Height)
        {
            throw new ArgumentOutOfRangeException(nameof(item));
        }

        Items.Add(item.Id, item);
    }

    internal void Move(AuthorId author, MoveCommand command)
    {
        int playerId = checked((int)author.Value);
        PlayerState player = Players[playerId];
        (int dx, int dy) = MazeRules.Delta(command.Direction);
        player = player with { X = player.X + dx, Y = player.Y + dy };
        foreach (ItemState item in Items.Values.ToArray())
        {
            if (item.X != player.X || item.Y != player.Y)
            {
                continue;
            }

            if (item.Type == ItemType.Coin)
            {
                player = player with { Score = player.Score + 1 };
                Items.Remove(item.Id);
            }
            else
            {
                player = player with { Health = player.Health - 1 };
            }
        }

        Players[playerId] = player;
    }
}

public static class MazeSession
{
    public static SessionHost Create(
        SessionId sessionId,
        ProtocolId protocolId,
        AuthorId author,
        MazeWorld world,
        ITransport? transport = null,
        ulong seed = 123,
        SessionMode mode = SessionMode.Local)
    {
#if NET10_0
        ArgumentNullException.ThrowIfNull(world);
#else
        if (world is null)
        {
            throw new ArgumentNullException(nameof(world));
        }
#endif

        var registry = new CommandRegistry();
        foreach (ICommandRegistration registration in GeneratedCommands.Registrations)
        {
            registry.Register(registration);
        }

        var session = new SessionHost(
            new SessionStart(sessionId, author, protocolId, 0, seed),
            registry,
            new MazeCommandPayloadHandler(),
            new RollbackSessionModel(new MazeSimulation(world), 0, historyDepth: 32),
            new MemoryCommandJournal(),
            transport: transport,
            mode: mode);
        session.Register<MoveCommand>(registry.GetId<MoveCommand>());
        session.Register<MoveCommand>(new MazeMoveValidator(world));
        session.Register<MoveCommand>(new MazeMoveExecutor(world));
        return session;
    }
}

public static class MazePayloadCodec
{
    public static byte[] Encode(MoveCommand command)
        => [(byte)command.Direction];

    public static bool TryDecode(ReadOnlySpan<byte> payload, out MoveCommand command)
    {
        if (payload.Length != 1)
        {
            command = new MoveCommand(default);
            return false;
        }

        command = new MoveCommand((MoveDirection)payload[0]);
        return true;
    }
}

internal static class MazeRules
{
    public static (int X, int Y) Delta(MoveDirection direction) => direction switch
    {
        MoveDirection.Up => (0, -1),
        MoveDirection.Right => (1, 0),
        MoveDirection.Down => (0, 1),
        MoveDirection.Left => (-1, 0),
        _ => throw new ArgumentOutOfRangeException(nameof(direction))
    };
}

internal sealed class MazeMoveValidator(MazeWorld world) : ICommandValidator<MoveCommand>
{
    public bool Validate(in Command<MoveCommand> command, in CommandValidationContext context)
    {
        uint playerId = command.Header.Key.AuthorId.Value;
        if (command.Payload.Direction > MoveDirection.Left || playerId > int.MaxValue || !world.Players.TryGetValue((int)playerId, out PlayerState player))
        {
            return false;
        }

        (int dx, int dy) = MazeRules.Delta(command.Payload.Direction);
        return player.X + dx >= 0 && player.X + dx < world.Width
            && player.Y + dy >= 0 && player.Y + dy < world.Height;
    }
}

internal sealed class MazeMoveExecutor(MazeWorld world) : ICommandExecutor<MoveCommand>
{
    public void Execute(in Command<MoveCommand> command) => world.Move(command.Header.Key.AuthorId, command.Payload);
}

internal sealed class MazeCommandPayloadHandler : ICommandPayloadHandler
{
    public void Write<T>(in T payload, IBufferWriter<byte> output)
    {
        if (typeof(T) != typeof(MoveCommand))
        {
            throw new NotSupportedException($"No Maze payload codec for {typeof(T)}.");
        }

        Span<byte> destination = output.GetSpan(1);
        destination[0] = (byte)((MoveCommand)(object)payload).Direction;
        output.Advance(1);
    }

    public T Read<T>(ReadOnlySpan<byte> payload)
    {
        if (typeof(T) != typeof(MoveCommand) || !MazePayloadCodec.TryDecode(payload, out MoveCommand command))
        {
            throw new ArgumentException("Maze move payload must contain exactly one direction byte.", nameof(payload));
        }

        return (T)(object)command;
    }
}

internal sealed class MazeSimulation(MazeWorld world) : ISimulation
{
    public void Tick(long simulationStep)
    {
    }

    public void Save(IBufferWriter<byte> output)
    {
        Span<byte> header = output.GetSpan(12);
        BinaryPrimitives.WriteInt32LittleEndian(header, world.Width);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], world.Height);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], world.Players.Count);
        output.Advance(12);
        foreach (PlayerState player in world.Players.Values.OrderBy(player => player.Id))
        {
            Span<byte> bytes = output.GetSpan(20);
            BinaryPrimitives.WriteInt32LittleEndian(bytes, player.Id);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[4..], player.X);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[8..], player.Y);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[12..], player.Score);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[16..], player.Health);
            output.Advance(20);
        }

        Span<byte> itemCount = output.GetSpan(4);
        BinaryPrimitives.WriteInt32LittleEndian(itemCount, world.Items.Count);
        output.Advance(4);
        foreach (ItemState item in world.Items.Values.OrderBy(item => item.Id))
        {
            Span<byte> bytes = output.GetSpan(17);
            BinaryPrimitives.WriteInt32LittleEndian(bytes, item.Id);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[4..], item.X);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[8..], item.Y);
            bytes[12] = (byte)item.Type;
            bytes[13..17].Clear();
            output.Advance(17);
        }
    }

    public void Load(ReadOnlySpan<byte> state)
    {
        int offset = 0;
        int width = ReadInt32(state, ref offset);
        int height = ReadInt32(state, ref offset);
        if (width != world.Width || height != world.Height)
        {
            throw new ArgumentException("Maze dimensions do not match this simulation.", nameof(state));
        }

        int playerCount = ReadInt32(state, ref offset);
        world.Players.Clear();
        for (int index = 0; index < playerCount; index++)
        {
            var player = new PlayerState(ReadInt32(state, ref offset), ReadInt32(state, ref offset), ReadInt32(state, ref offset), ReadInt32(state, ref offset), ReadInt32(state, ref offset));
            world.Players.Add(player.Id, player);
        }

        int itemCount = ReadInt32(state, ref offset);
        world.Items.Clear();
        for (int index = 0; index < itemCount; index++)
        {
            int id = ReadInt32(state, ref offset);
            int x = ReadInt32(state, ref offset);
            int y = ReadInt32(state, ref offset);
            var item = new ItemState(id, x, y, (ItemType)state[offset++]);
            world.Items.Add(id, item);
            offset += 4;
        }

        if (offset != state.Length)
        {
            throw new ArgumentException("Maze state has trailing bytes.", nameof(state));
        }
    }

    private static int ReadInt32(ReadOnlySpan<byte> source, ref int offset)
    {
        int value = BinaryPrimitives.ReadInt32LittleEndian(source[offset..]);
        offset += sizeof(int);
        return value;
    }
}
