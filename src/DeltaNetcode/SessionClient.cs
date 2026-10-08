namespace Delta.Netcode;

/// <summary>Starts session synchronization and applies server messages for a client host.</summary>
public interface ISessionClient
{
    /// <summary>Gets the session controlled by this client.</summary>
    ISession Session { get; }

    /// <summary>Gets the client's join, synchronization or ready state.</summary>
    SessionClientState State { get; }

    /// <summary>Gets whether an initial snapshot or replay has been fully applied.</summary>
    bool IsReady { get; }

    /// <summary>Connects through a transport and requests a join or resume stream.</summary>
    /// <param name="transport">The application-owned transport used for this connection.</param>
    /// <param name="connectionId">The transport-specific server connection identifier.</param>
    void BeginJoin(ITransport transport, ulong connectionId);

    /// <summary>Applies one command, snapshot or synchronization message received from the server.</summary>
    /// <param name="message">The encoded server message.</param>
    /// <returns>The applied command result or synchronization processing result.</returns>
    CommandResult Receive(ReadOnlySpan<byte> message);

    /// <summary>Detaches the current connection while retaining the client resume cursor.</summary>
    void Disconnect();
}

/// <summary>Coordinates snapshot/replay synchronization for a client session host.</summary>
public sealed class SessionClient : ISessionClient
{
    private readonly SessionHost _session;
    private ulong? _connectionId;
    private ulong? _journalRevision;
    private bool _snapshotExpected;
    private bool _snapshotReceived;

    /// <summary>Creates a client coordinator for a client-mode session host.</summary>
    /// <param name="session">The session host receiving authoritative outcomes.</param>
    /// <exception cref="ArgumentException">The session is not in client mode.</exception>
    public SessionClient(SessionHost session)
    {
        Guard.ThrowIfNull(session, nameof(session));
        if (session.Mode != SessionMode.Client)
        {
            throw new ArgumentException("A session client requires a client-mode session host.", nameof(session));
        }

        _session = session;
        State = SessionClientState.Disconnected;
    }

    /// <inheritdoc />
    public ISession Session => _session;

    /// <inheritdoc />
    public SessionClientState State { get; private set; }

    /// <inheritdoc />
    public bool IsReady => State == SessionClientState.Ready;

    /// <inheritdoc />
    public void BeginJoin(ITransport transport, ulong connectionId)
    {
        Guard.ThrowIfNull(transport, nameof(transport));
        if (State is SessionClientState.Joining or SessionClientState.Synchronizing
            or SessionClientState.SessionNotFound or SessionClientState.ProtocolMismatch)
        {
            _journalRevision = null;
        }

        if (_connectionId is ulong previousConnection)
        {
            _session.DetachConnection(previousConnection);
        }

        _session.AttachTransport(transport);
        _session.AttachConnection(connectionId);
        _connectionId = connectionId;
        _snapshotExpected = false;
        _snapshotReceived = false;
        State = SessionClientState.Joining;

        var request = new SessionSyncRequest(
            _session.Start.SessionId,
            _session.Start.ProtocolId,
            new SessionResumeCursor(_session.CurrentStep, _journalRevision, _session.LastResolvedAuthorSequence));
        transport.Send(connectionId, CommandProtocol.EncodeSyncRequest(request));
    }

    /// <inheritdoc />
    public void Disconnect()
    {
        if (State != SessionClientState.Ready)
        {
            _journalRevision = null;
        }

        if (_connectionId is ulong connectionId)
        {
            _session.DetachConnection(connectionId);
            _connectionId = null;
        }

        State = SessionClientState.Disconnected;
    }

    /// <inheritdoc />
    public CommandResult Receive(ReadOnlySpan<byte> message)
    {
        if (CommandProtocol.TryReadSyncStatus(message, out SessionSyncStatus syncStatus, out SessionId sessionId, out ProtocolId protocolId))
        {
            return ApplySyncStatus(syncStatus, sessionId, protocolId);
        }

        if (CommandProtocol.TryReadSnapshot(message, out SessionSnapshot? snapshot))
        {
            if (State != SessionClientState.Synchronizing || !_snapshotExpected || _snapshotReceived)
            {
                return CommandResult.InvalidMessage;
            }

            try
            {
                _session.Restore(snapshot!);
                _snapshotReceived = true;
                return CommandResult.Accepted;
            }
            catch (ArgumentException)
            {
                return CommandResult.InvalidMessage;
            }
        }

        if (CommandProtocol.TryReadOutcome(message, out CommandOutcome outcome, out ReadOnlySpan<byte> finalPayload))
        {
            return _session.ApplyOutcome(outcome, finalPayload).Result;
        }

        if (CommandProtocol.TryReadSyncComplete(message, out sessionId, out ulong? revision, out long currentStep))
        {
            if (sessionId != _session.Start.SessionId
                || State != SessionClientState.Synchronizing
                || (_snapshotExpected && !_snapshotReceived)
                || currentStep < _session.CurrentStep)
            {
                return CommandResult.InvalidMessage;
            }

            if (currentStep > _session.CurrentStep)
            {
                _session.Tick(currentStep);
            }

            _journalRevision = revision;
            State = SessionClientState.Ready;
            if (_connectionId is ulong connectionId)
            {
                _session.ResendPending(connectionId);
            }

            return CommandResult.Accepted;
        }

        return CommandResult.InvalidMessage;
    }

    private CommandResult ApplySyncStatus(SessionSyncStatus status, SessionId sessionId, ProtocolId protocolId)
    {
        if (sessionId != _session.Start.SessionId)
        {
            return CommandResult.InvalidMessage;
        }

        if (status == SessionSyncStatus.SessionNotFound)
        {
            _journalRevision = null;
            State = SessionClientState.SessionNotFound;
            return CommandResult.Rejected;
        }

        if (status == SessionSyncStatus.ProtocolMismatch)
        {
            _journalRevision = null;
            State = SessionClientState.ProtocolMismatch;
            return CommandResult.Conflict;
        }

        if (protocolId != _session.Start.ProtocolId || State != SessionClientState.Joining)
        {
            return CommandResult.InvalidMessage;
        }

        _snapshotExpected = status == SessionSyncStatus.Snapshot;
        _snapshotReceived = !_snapshotExpected;
        State = SessionClientState.Synchronizing;
        return CommandResult.Accepted;
    }
}
