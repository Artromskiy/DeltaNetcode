using System.Buffers;

namespace Delta.Netcode;

/// <summary>Serializes and deserializes application-defined command payloads.</summary>
public interface ICommandPayloadHandler
{
    /// <summary>Serializes a command payload into the supplied buffer.</summary>
    /// <typeparam name="T">The command payload type.</typeparam>
    /// <param name="payload">The payload value to serialize.</param>
    /// <param name="output">The destination buffer writer.</param>
    void Write<T>(in T payload, IBufferWriter<byte> output);

    /// <summary>Deserializes a command payload from its encoded bytes.</summary>
    /// <typeparam name="T">The expected command payload type.</typeparam>
    /// <param name="payload">The encoded payload bytes.</param>
    /// <returns>The decoded payload value.</returns>
    T Read<T>(ReadOnlySpan<byte> payload);
}

/// <summary>Validates commands with a shared rule independent of command type.</summary>
public interface ICommandValidator
{
    /// <summary>Returns whether a command satisfies this validation rule.</summary>
    /// <typeparam name="T">The command payload type.</typeparam>
    /// <param name="command">The command and its session header.</param>
    /// <returns><see langword="true"/> when the command is valid.</returns>
    bool Validate<T>(in Command<T> command);
}

/// <summary>Validates commands of one payload type.</summary>
/// <typeparam name="T">The command payload type.</typeparam>
public interface ICommandValidator<T>
{
    /// <summary>Returns whether a command satisfies this validation rule.</summary>
    /// <param name="command">The command and its session header.</param>
    /// <returns><see langword="true"/> when the command is valid.</returns>
    bool Validate(in Command<T> command);
}

/// <summary>Mutates a command payload before an authority accepts and records it.</summary>
/// <typeparam name="T">The command payload type.</typeparam>
public interface ICommandMutator<T>
{
    /// <summary>Applies deterministic authoritative changes to a command payload.</summary>
    /// <param name="payload">The payload being changed.</param>
    /// <param name="preparation">The transactional ID and seed allocator for this command.</param>
    void Mutate(ref T payload, CommandPreparation preparation);
}

/// <summary>Executes an accepted command during its scheduled simulation step.</summary>
/// <typeparam name="T">The command payload type.</typeparam>
public interface ICommandExecutor<T>
{
    /// <summary>Applies the accepted command to application-owned simulation state.</summary>
    /// <param name="command">The accepted command and authoritative header.</param>
    void Execute(in Command<T> command);
}

/// <summary>Allocates deterministic IDs and seeds for a command being prepared by authority.</summary>
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
