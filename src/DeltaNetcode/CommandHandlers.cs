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

    public CommandPreparation(ulong nextId, ulong seed)
    {
        _nextId = nextId;
        _randomState = seed;
    }

    public ulong NextId() => _nextId++;

    public ulong NextSeed()
    {
        _randomState += 0x9E3779B97F4A7C15UL;
        ulong value = _randomState;
        value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
        return value ^ (value >> 31);
    }

    internal (ulong NextId, ulong RandomState) Capture() => (_nextId, _randomState);

    internal CommandPreparation Fork()
    {
        var copy = new CommandPreparation(_nextId, _randomState);
        return copy;
    }

    internal void Restore((ulong NextId, ulong RandomState) state)
    {
        _nextId = state.NextId;
        _randomState = state.RandomState;
    }
}
