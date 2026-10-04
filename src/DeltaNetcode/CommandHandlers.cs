using System.Buffers;

namespace Delta.Netcode;

public interface ICommandPayloadHandler
{
    void Write<T>(in T payload, IBufferWriter<byte> output);

    T Read<T>(ReadOnlySpan<byte> payload);
}

public interface ICommandValidator
{
    bool Validate<T>(in Command<T> command);
}

public interface ICommandValidator<T>
{
    bool Validate(in Command<T> command);
}

public interface ICommandMutator<T>
{
    void Mutate(ref T payload, CommandPreparation preparation);
}

public interface ICommandExecutor<T>
{
    void Execute(in Command<T> command);
}

public sealed class CommandPreparation
{
    private ulong _nextId;
    private ulong _randomState;

    /// <summary>
    /// Creates command preparation from previously captured state.
    /// </summary>
    public CommandPreparation(CommandPreparationState state)
    {
        _nextId = state.NextId;
        _randomState = state.RandomState;
    }

    /// <summary>
    /// Creates command preparation from the first identifier and random seed.
    /// </summary>
    public CommandPreparation(ulong nextId, ulong seed)
        : this(new CommandPreparationState(nextId, seed))
    {
    }

    /// <summary>
    /// Returns the next identifier and advances the allocator by one.
    /// </summary>
    public ulong NextId()
    {
        ulong id = _nextId;
        _nextId = checked(_nextId + 1);
        return id;
    }

    /// <summary>
    /// Reserves a contiguous range and returns its first identifier.
    /// </summary>
    public ulong ReserveIds(uint count)
    {
        if (count == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "At least one ID must be reserved.");
        }

        ulong firstId = _nextId;
        _nextId = checked(_nextId + count);
        return firstId;
    }

    /// <summary>
    /// Returns the next deterministic seed.
    /// </summary>
    public ulong NextSeed()
    {
        _randomState += 0x9E3779B97F4A7C15UL;
        ulong value = _randomState;
        value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
        return value ^ (value >> 31);
    }

    /// <summary>
    /// Captures the allocator and random state.
    /// </summary>
    public CommandPreparationState Capture() => new(_nextId, _randomState);

    internal CommandPreparation Fork()
    {
        return new CommandPreparation(Capture());
    }

    internal void Restore(CommandPreparationState state)
    {
        _nextId = state.NextId;
        _randomState = state.RandomState;
    }
}
