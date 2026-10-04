namespace Delta.Netcode;

/// <summary>Stores command requests, authoritative outcomes and cancellation tombstones.</summary>
public interface ICommandJournal
{
    /// <summary>Looks up a command record by its stable key.</summary>
    /// <param name="key">The command key to find.</param>
    /// <param name="record">Receives the stored record when found; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when a record exists.</returns>
    bool TryGet(CommandKey key, out JournalRecord? record);

    /// <summary>Adds a command record; the key must not already exist.</summary>
    /// <param name="record">The command record to append.</param>
    void Append(in JournalRecord record);

    /// <summary>Replaces the record stored for the same command key.</summary>
    /// <param name="record">The updated command record.</param>
    void Replace(in JournalRecord record);

    /// <summary>Marks a command cancelled or records a tombstone for a not-yet-seen key.</summary>
    /// <param name="key">The command key to cancel.</param>
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

/// <summary>Stores the request and authoritative result for one command.</summary>
public sealed class JournalRecord
{
    private readonly byte[] _requestPayload;
    private readonly byte[] _finalPayload;

    /// <summary>Creates an owned copy of a command's request and final payloads.</summary>
    /// <param name="header">The authoritative command header.</param>
    /// <param name="result">The command result.</param>
    /// <param name="requestPayload">The original submitted payload bytes.</param>
    /// <param name="finalPayload">The final accepted payload bytes, or empty for a non-accepted result.</param>
    /// <param name="requestHeader">The original proposal header when it differs from the authoritative header.</param>
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

    /// <summary>Gets the authoritative command header.</summary>
    public CommandHeader Header { get; }

    /// <summary>Gets the original proposal header.</summary>
    public CommandHeader RequestHeader { get; }

    /// <summary>Gets the recorded decision for the command.</summary>
    public CommandResult Result { get; }

    /// <summary>Gets the owned bytes submitted in the original request.</summary>
    public ReadOnlyMemory<byte> RequestPayload => _requestPayload;

    /// <summary>Gets the owned final payload bytes used for accepted execution.</summary>
    public ReadOnlyMemory<byte> FinalPayload => _finalPayload;

    /// <summary>Gets whether this record is a cancellation tombstone or was cancelled.</summary>
    public bool IsCancelled { get; internal set; }
}

/// <summary>Stores command records in memory for one session lifetime.</summary>
public sealed class MemoryCommandJournal : ICommandJournal
{
    private readonly Dictionary<CommandKey, JournalRecord> _records = [];

    /// <inheritdoc />
    public bool TryGet(CommandKey key, out JournalRecord? record)
        => _records.TryGetValue(key, out record);

    /// <inheritdoc />
    public void Append(in JournalRecord record)
    {
        Guard.ThrowIfNull(record, nameof(record));
        if (!_records.TryAdd(record.Header.Key, record))
        {
            throw new InvalidOperationException($"Command key '{record.Header.Key}' already exists in the journal.");
        }
    }

    /// <inheritdoc />
    public void Replace(in JournalRecord record)
    {
        Guard.ThrowIfNull(record, nameof(record));

        _records[record.Header.Key] = record;
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
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

    /// <inheritdoc />
    public void Clear() => _records.Clear();
}

/// <summary>Sends encoded session messages over an application-owned transport.</summary>
public interface ITransport
{
    /// <summary>Sends a message to one transport connection.</summary>
    /// <param name="connectionId">The transport-specific destination connection.</param>
    /// <param name="message">The encoded message bytes, borrowed for the duration of the call.</param>
    void Send(ulong connectionId, ReadOnlySpan<byte> message);
}

/// <summary>Provides deterministic simulation state save, load and fixed-step updates.</summary>
public interface ISimulation
{
    /// <summary>Advances the application simulation by the specified step.</summary>
    /// <param name="simulationStep">The step being applied.</param>
    void Tick(long simulationStep);

    /// <summary>Writes the complete simulation state to the supplied buffer.</summary>
    /// <param name="output">The destination buffer writer.</param>
    void Save(System.Buffers.IBufferWriter<byte> output);

    /// <summary>Replaces the simulation state from previously saved bytes.</summary>
    /// <param name="state">The serialized simulation state.</param>
    void Load(ReadOnlySpan<byte> state);
}

/// <summary>Schedules commands and owns rollback or snapshot state for a session.</summary>
public interface ISessionModel
{
    /// <summary>Adjusts a proposed step to a schedulable step or rejects it.</summary>
    /// <param name="simulationStep">The proposed step, updated when the model reschedules it.</param>
    /// <returns><see langword="true"/> when the command can be scheduled.</returns>
    bool TrySchedule(ref long simulationStep);

    /// <summary>Returns whether a command at the given header can still be cancelled.</summary>
    /// <param name="header">The command's scheduled header.</param>
    /// <returns><see langword="true"/> when the command is within the mutable history window.</returns>
    bool CanCancel(in CommandHeader header);

    /// <summary>Adds or replaces an execution entry in the model.</summary>
    /// <param name="command">The command entry to schedule.</param>
    void SetCommand(CommandEntry command);

    /// <summary>Removes a scheduled command by key.</summary>
    /// <param name="key">The command key to remove.</param>
    void Remove(CommandKey key);

    /// <summary>Advances the model through the requested simulation step.</summary>
    /// <param name="simulationStep">The last step to apply.</param>
    void Tick(long simulationStep);

    /// <summary>Serializes the current model state.</summary>
    /// <param name="output">The destination buffer writer.</param>
    void Save(System.Buffers.IBufferWriter<byte> output);

    /// <summary>
    /// Writes a replayable baseline and returns the last completed step represented by it.
    /// Commands after that step can be replayed against the baseline.
    /// </summary>
    long SaveReplayAnchor(System.Buffers.IBufferWriter<byte> output);

    /// <summary>Restores model state from a serialized representation.</summary>
    /// <param name="state">The previously saved model state.</param>
    void Load(ReadOnlySpan<byte> state);
}

/// <summary>Owns a command's final payload and invokes its executor when scheduled.</summary>
public sealed class CommandEntry
{
    private readonly ICommandEntryInvoker _invoker;

    internal CommandEntry(CommandHeader header, ReadOnlySpan<byte> finalPayload, ICommandEntryInvoker invoker)
    {
        Header = header;
        FinalPayload = finalPayload.ToArray();
        _invoker = invoker;
    }

    /// <summary>Gets the authoritative header used to schedule this command.</summary>
    public CommandHeader Header { get; }

    /// <summary>Gets the owned final payload bytes used for execution and replay.</summary>
    public ReadOnlyMemory<byte> FinalPayload { get; }

    /// <summary>Executes the command payload through its registered executor.</summary>
    public void Execute() => _invoker.Execute(Header, FinalPayload.Span);
}

internal interface ICommandEntryInvoker
{
    void Execute(CommandHeader header, ReadOnlySpan<byte> finalPayload);
}
