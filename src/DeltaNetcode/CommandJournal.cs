namespace Delta.Netcode;

public interface ICommandJournal
{
    bool TryGet(CommandKey key, out JournalRecord? record);

    void Append(in JournalRecord record);

    void Replace(in JournalRecord record);

    void Cancel(CommandKey key);

}

public sealed class JournalRecord
{
    private readonly byte[] _requestPayload;
    private readonly byte[] _finalPayload;

    public JournalRecord(
        CommandHeader header,
        CommandResult result,
        ReadOnlySpan<byte> requestPayload,
        ReadOnlySpan<byte> finalPayload,
        CommandHeader? requestHeader = null)
    {
        Header = header;
        RequestHeader = requestHeader ?? header;
        Result = result;
        _requestPayload = requestPayload.ToArray();
        _finalPayload = finalPayload.ToArray();
    }

    public CommandHeader Header { get; }

    public CommandHeader RequestHeader { get; }

    public CommandResult Result { get; }

    public ReadOnlyMemory<byte> RequestPayload => _requestPayload;

    public ReadOnlyMemory<byte> FinalPayload => _finalPayload;

    public bool IsCancelled { get; internal set; }
}

public sealed class MemoryCommandJournal : ICommandJournal
{
    private readonly Dictionary<CommandKey, JournalRecord> _records = [];

    public bool TryGet(CommandKey key, out JournalRecord? record)
        => _records.TryGetValue(key, out record);

    public void Append(in JournalRecord record)
    {
        Guard.ThrowIfNull(record, nameof(record));
        if (!_records.TryAdd(record.Header.Key, record))
        {
            throw new InvalidOperationException($"Command key '{record.Header.Key}' already exists in the journal.");
        }
    }

    public void Replace(in JournalRecord record)
    {
        Guard.ThrowIfNull(record, nameof(record));

        _records[record.Header.Key] = record;
    }

    public void Cancel(CommandKey key)
    {
        if (_records.TryGetValue(key, out JournalRecord? record))
        {
            record.IsCancelled = true;
            return;
        }

        var tombstoneHeader = new CommandHeader(key, 0, long.MinValue, 0);
        _records.Add(key, new JournalRecord(tombstoneHeader, CommandResult.Cancelled, [], []) { IsCancelled = true });
    }
}

public interface ITransport
{
    void Send(ulong connectionId, ReadOnlySpan<byte> message);
}

public interface ISimulation
{
    void Tick(long simulationStep);

    void Save(System.Buffers.IBufferWriter<byte> output);

    void Load(ReadOnlySpan<byte> state);
}

public interface ISessionModel
{
    bool TrySchedule(ref long simulationStep);

    bool CanCancel(in CommandHeader header);

    void SetCommand(CommandEntry command);

    void Remove(CommandKey key);

    void Tick(long simulationStep);

    void Save(System.Buffers.IBufferWriter<byte> output);

    void Load(ReadOnlySpan<byte> state);
}

public sealed class CommandEntry
{
    private readonly ICommandEntryInvoker _invoker;

    internal CommandEntry(CommandHeader header, ReadOnlySpan<byte> finalPayload, ICommandEntryInvoker invoker)
    {
        Header = header;
        FinalPayload = finalPayload.ToArray();
        _invoker = invoker;
    }

    public CommandHeader Header { get; }

    public ReadOnlyMemory<byte> FinalPayload { get; }

    public void Execute() => _invoker.Execute(Header, FinalPayload.Span);
}

internal interface ICommandEntryInvoker
{
    void Execute(CommandHeader header, ReadOnlySpan<byte> finalPayload);
}
