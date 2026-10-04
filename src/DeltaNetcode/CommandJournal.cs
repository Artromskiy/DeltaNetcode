namespace Delta.Netcode;

public interface ICommandJournal
{
    bool TryGet(CommandKey key, out JournalRecord? record);

    void Append(in JournalRecord record);

    void Replace(in JournalRecord record);

    void Cancel(CommandKey key);

    /// <summary>
    /// Returns accepted, non-cancelled records after a snapshot boundary, in simulation order.
    /// </summary>
    /// <remarks>
    /// Records later than <paramref name="cursor"/> are those at a later step or with a higher
    /// authoritative session order. This includes future commands already accepted at capture time
    /// and commands accepted later inside the rollback window.
    /// </remarks>
    IEnumerable<JournalRecord> ReadAcceptedAfter(CommandCursor cursor);

    /// <summary>
    /// Removes all records from the journal.
    /// </summary>
    void Clear();
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

    public IEnumerable<JournalRecord> ReadAcceptedAfter(CommandCursor cursor)
    {
        var records = new List<JournalRecord>();
        foreach (JournalRecord record in _records.Values)
        {
            if (record.Result != CommandResult.Accepted || record.IsCancelled)
            {
                continue;
            }

            // Orders are session-wide and monotonic. Step also includes accepted future work
            // that was already in the journal when a snapshot was captured.
            if (record.Header.Step > cursor.Step || record.Header.Order > cursor.Order)
            {
                records.Add(record);
            }
        }

        records.Sort(static (left, right) =>
        {
            int stepComparison = left.Header.Step.CompareTo(right.Header.Step);
            if (stepComparison != 0)
            {
                return stepComparison;
            }

            int orderComparison = left.Header.Order.CompareTo(right.Header.Order);
            if (orderComparison != 0)
            {
                return orderComparison;
            }

            int authorComparison = left.Header.Key.AuthorId.Value.CompareTo(right.Header.Key.AuthorId.Value);
            return authorComparison != 0
                ? authorComparison
                : left.Header.Key.Sequence.CompareTo(right.Header.Key.Sequence);
        });
        return records;
    }

    public void Clear() => _records.Clear();
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

    /// <summary>
    /// Writes a replayable baseline and returns the last completed step represented by it.
    /// Commands after that step can be replayed against the baseline.
    /// </summary>
    long SaveReplayAnchor(System.Buffers.IBufferWriter<byte> output);

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
