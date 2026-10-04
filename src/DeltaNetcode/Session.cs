using System.Buffers;

namespace Delta.Netcode;

/// <summary>Controls command registration, submission, simulation and replay for one session.</summary>
public interface ISession
{
    /// <summary>Gets the last simulation step applied by this session.</summary>
    long CurrentStep { get; }

    /// <summary>Registers a command payload type and its stable ID.</summary>
    /// <typeparam name="T">The command payload type.</typeparam>
    /// <param name="id">The non-zero stable command ID.</param>
    void Register<T>(ulong id);

    /// <summary>Registers a validator that applies to every command type.</summary>
    /// <param name="validator">The common command validator.</param>
    void Register(ICommandValidator validator);

    /// <summary>Registers a validator for one command payload type.</summary>
    /// <typeparam name="T">The command payload type.</typeparam>
    /// <param name="validator">The typed validator.</param>
    void Register<T>(ICommandValidator<T> validator);

    /// <summary>Registers a mutator for one command payload type.</summary>
    /// <typeparam name="T">The command payload type.</typeparam>
    /// <param name="mutator">The typed command mutator.</param>
    void Register<T>(ICommandMutator<T> mutator);

    /// <summary>Registers an executor for one command payload type.</summary>
    /// <typeparam name="T">The command payload type.</typeparam>
    /// <param name="executor">The typed command executor.</param>
    void Register<T>(ICommandExecutor<T> executor);

    /// <summary>Creates and submits a command for a simulation step.</summary>
    /// <typeparam name="T">The command payload type.</typeparam>
    /// <param name="payload">The command payload, serialized during this call.</param>
    /// <param name="simulationStep">The requested simulation step.</param>
    /// <returns>The stable key assigned to the command.</returns>
    CommandKey Send<T>(in T payload, long simulationStep);

    /// <summary>Requests cancellation of a command authored by this session host.</summary>
    /// <param name="key">The command key to cancel.</param>
    /// <returns>The cancellation outcome.</returns>
    CommandResult Cancel(CommandKey key);

    /// <summary>Attaches a transport connection used to send session messages.</summary>
    /// <param name="connectionId">The transport-specific connection identifier.</param>
    void AttachConnection(ulong connectionId);

    /// <summary>Detaches a previously attached transport connection.</summary>
    /// <param name="connectionId">The transport-specific connection identifier.</param>
    /// <returns><see langword="true"/> if the connection was attached.</returns>
    bool DetachConnection(ulong connectionId);

    /// <summary>Applies an authoritative command outcome and updates predicted state.</summary>
    /// <param name="outcome">The server outcome to apply.</param>
    /// <param name="finalPayload">The accepted command payload, or an empty span for non-accepted outcomes.</param>
    /// <returns>The applied outcome, or a failure result if it is incompatible with this session.</returns>
    CommandOutcome ApplyOutcome(in CommandOutcome outcome, ReadOnlySpan<byte> finalPayload);

    /// <summary>Advances the session model through the specified simulation step.</summary>
    /// <param name="simulationStep">The last simulation step to apply.</param>
    void Tick(long simulationStep);

    /// <summary>
    /// Captures a replayable session baseline and the command preparation state.
    /// </summary>
    SessionSnapshot CaptureSnapshot();

    /// <summary>
    /// Restores this session from a compatible snapshot and discards prior command history.
    /// </summary>
    void Restore(SessionSnapshot snapshot);

    /// <summary>Dispatches a command type ID to a visitor without exposing the command registry.</summary>
    /// <typeparam name="TVisitor">The visitor type to dispatch.</typeparam>
    /// <param name="typeId">The registered command type ID.</param>
    /// <param name="visitor">The visitor that handles the registered command type.</param>
    /// <exception cref="KeyNotFoundException">The command type ID is not registered.</exception>
    void VisitCommandType<TVisitor>(ulong typeId, ref TVisitor visitor)
        where TVisitor : ICommandVisitor;

    /// <summary>Reads accepted, non-cancelled command records after a snapshot cursor.</summary>
    /// <param name="cursor">The snapshot cursor that defines which accepted records have already been applied.</param>
    /// <returns>Accepted records after the cursor, in deterministic simulation order.</returns>
    IEnumerable<JournalRecord> ReadAcceptedAfter(CommandCursor cursor);
}

/// <summary>Owns command registration, authority state, journal and simulation model for one session.</summary>
public sealed class SessionHost : ISession
{
    private readonly Dictionary<Type, ICommandPolicy> _policies = [];
    private readonly List<ICommandValidator> _validators = [];
    private readonly ICommandRegistry _commands;
    private readonly ICommandPayloadHandler _payloadHandler;
    private readonly ICommandJournal _journal;
    private readonly ISessionModel _model;
    private readonly CommandPreparation _preparation;
    private ITransport? _transport;
    private readonly HashSet<ulong> _connections = [];
    private ulong _nextSequence;
    private uint _nextOrder = 1;
    private long _currentStep;
    private CommandCursor? _snapshotCursor;

    /// <summary>Creates a session host with its command, payload, model and journal dependencies.</summary>
    /// <param name="start">The session identity and initial deterministic state.</param>
    /// <param name="commands">The command registry used for type and ID lookup.</param>
    /// <param name="payloadHandler">The application codec for typed command payloads.</param>
    /// <param name="model">The session model that schedules and applies commands.</param>
    /// <param name="journal">The command history store.</param>
    /// <param name="firstSequence">The first command sequence assigned by this host.</param>
    /// <param name="transport">The optional transport used to send session messages.</param>
    /// <param name="mode">The local, client or server authority mode.</param>
    /// <param name="preparationState">The initial deterministic ID and seed state.</param>
    public SessionHost(
        SessionStart start,
        ICommandRegistry commands,
        ICommandPayloadHandler payloadHandler,
        ISessionModel model,
        ICommandJournal journal,
        ulong firstSequence = 0,
        ITransport? transport = null,
        SessionMode mode = SessionMode.Local,
        CommandPreparationState? preparationState = null)
    {
        Start = start;
        Guard.ThrowIfNull(commands, nameof(commands));
        Guard.ThrowIfNull(payloadHandler, nameof(payloadHandler));
        Guard.ThrowIfNull(model, nameof(model));
        Guard.ThrowIfNull(journal, nameof(journal));
        _commands = commands;
        _payloadHandler = payloadHandler;
        _model = model;
        _journal = journal;
        _transport = transport;
        Mode = mode;
        _nextSequence = firstSequence;
        _currentStep = checked(start.Step - 1);
        _preparation = new CommandPreparation(preparationState ?? new CommandPreparationState(1, start.Seed));
    }

    /// <summary>Gets the immutable initial configuration of this session.</summary>
    public SessionStart Start { get; }

    /// <summary>Gets the authority mode of this session.</summary>
    public SessionMode Mode { get; }

    /// <summary>Gets the last simulation step applied by this session.</summary>
    public long CurrentStep => _currentStep;

    /// <summary>Attaches a transport connection used to send session messages.</summary>
    /// <param name="connectionId">The transport-specific connection identifier.</param>
    /// <inheritdoc />
    public void AttachConnection(ulong connectionId) => _connections.Add(connectionId);

    /// <summary>Detaches a previously attached transport connection.</summary>
    /// <param name="connectionId">The transport-specific connection identifier.</param>
    /// <returns><see langword="true"/> if the connection was attached.</returns>
    /// <inheritdoc />
    public bool DetachConnection(ulong connectionId) => _connections.Remove(connectionId);

    internal void AttachTransport(ITransport transport)
    {
        Guard.ThrowIfNull(transport, nameof(transport));
        _transport = transport;
    }

    /// <inheritdoc />
    public void Register(ICommandValidator validator)
    {
        Guard.ThrowIfNull(validator, nameof(validator));

        _validators.Add(validator);
    }

    /// <inheritdoc />
    public void Register<T>(ulong id)
    {
        RegisterCommandType<T>(id);
    }

    /// <inheritdoc />
    public void Register<T>(ICommandValidator<T> validator)
        => GetOrCreatePolicy<T>().Register(validator);

    /// <inheritdoc />
    public void Register<T>(ICommandMutator<T> mutator)
        => GetOrCreatePolicy<T>().Register(mutator);

    /// <inheritdoc />
    public void Register<T>(ICommandExecutor<T> executor)
        => GetOrCreatePolicy<T>().Register(executor);

    private void RegisterCommandType<T>(ulong id)
    {
        if (TryGetRegisteredCommandId<T>(out ulong registeredId))
        {
            if (registeredId != id)
            {
                throw new InvalidOperationException($"Command '{typeof(T)}' is already registered with a different ID.");
            }

            return;
        }

        _commands.Register<T>(id);
    }

    private bool TryGetRegisteredCommandId<T>(out ulong id)
    {
        try
        {
            id = _commands.GetId<T>();
            return true;
        }
        catch (KeyNotFoundException)
        {
            id = default;
            return false;
        }
    }

    private CommandPolicy<T> GetOrCreatePolicy<T>()
    {
        _commands.GetId<T>();
        if (_policies.TryGetValue(typeof(T), out ICommandPolicy? existing))
        {
            return existing is CommandPolicy<T> typedPolicy
                ? typedPolicy
                : throw new InvalidOperationException($"Command policy type mismatch for '{typeof(T)}'.");
        }

        var policy = new CommandPolicy<T>();
        _policies.Add(typeof(T), policy);
        return policy;
    }

    /// <inheritdoc />
    public CommandKey Send<T>(in T payload, long simulationStep)
    {
        ulong typeId = _commands.GetId<T>();
        var key = new CommandKey(Start.SessionId, Start.AuthorId, checked(_nextSequence++));
        byte[] bytes = Serialize(payload);
        if (Mode == SessionMode.Client)
        {
            var header = new CommandHeader(key, typeId, simulationStep, GetNextOrder());
            if (IsPredicted(typeId))
            {
                var record = new JournalRecord(header, CommandResult.Accepted, bytes, bytes);
                _journal.Append(record);
                _model.SetCommand(GetPolicy<T>().CreateEntry(header, bytes, _payloadHandler));
            }

            Broadcast(CommandProtocol.EncodeProposal(header, bytes));
            return key;
        }

        var requestHeader = new CommandHeader(key, typeId, simulationStep, 0);
        CommandOutcome outcome = Accept(requestHeader, requestHeader, bytes, payload);
        if (Mode == SessionMode.Server && outcome.Result == CommandResult.Accepted
            && _journal.TryGet(key, out JournalRecord? accepted))
        {
            Broadcast(CommandProtocol.EncodeOutcome(outcome, accepted!.FinalPayload.Span));
        }

        return key;
    }

    /// <summary>Validates and records a proposal received from an authenticated author.</summary>
    /// <param name="authenticatedAuthor">The author established by the connection binding.</param>
    /// <param name="proposal">The client-supplied command header.</param>
    /// <param name="requestPayload">The serialized command payload.</param>
    /// <returns>The authoritative result for the proposal.</returns>
    public CommandOutcome Receive(AuthorId authenticatedAuthor, in CommandHeader proposal, ReadOnlySpan<byte> requestPayload)
    {
        if (Mode != SessionMode.Server)
        {
            return new CommandOutcome(CommandResult.Rejected, proposal);
        }

        if (proposal.Key.SessionId != Start.SessionId)
        {
            return new CommandOutcome(CommandResult.InvalidMessage, proposal);
        }

        var key = new CommandKey(Start.SessionId, authenticatedAuthor, proposal.Key.Sequence);
        if (_journal.TryGet(key, out JournalRecord? previous))
        {
            if (previous!.IsCancelled)
            {
                return new CommandOutcome(CommandResult.Cancelled, previous.Header);
            }

            bool sameRequest = previous.RequestHeader.TypeId == proposal.TypeId
                && previous.RequestHeader.Step == proposal.Step
                && previous.RequestHeader.Order == proposal.Order
                && previous.RequestPayload.Span.SequenceEqual(requestPayload);
            return sameRequest
                ? new CommandOutcome(previous.Result, previous.Header)
                : new CommandOutcome(CommandResult.Conflict, previous.Header);
        }

        if (!_commands.TryGet(proposal.TypeId, out ICommandRegistration? registration))
        {
            var unknown = new CommandHeader(key, proposal.TypeId, proposal.Step, proposal.Order);
            _journal.Append(new JournalRecord(unknown, CommandResult.UnknownType, requestPayload, []));
            return new CommandOutcome(CommandResult.UnknownType, unknown);
        }

        var proposedHeader = new CommandHeader(key, registration!.Id, proposal.Step, proposal.Order);
        var requestHeader = new CommandHeader(key, proposal.TypeId, proposal.Step, proposal.Order);
        var visitor = new ReceiveVisitor(this, proposedHeader, requestHeader, requestPayload.ToArray());
        registration.Visit(ref visitor);
        return visitor.Outcome;
    }

    /// <inheritdoc />
    public CommandResult Cancel(CommandKey key) => Cancel(Start.AuthorId, key);

    /// <summary>Requests cancellation on behalf of a specified author.</summary>
    /// <param name="requester">The authenticated author making the request.</param>
    /// <param name="key">The command key to cancel.</param>
    /// <returns>The cancellation outcome.</returns>
    public CommandResult Cancel(AuthorId requester, CommandKey key)
    {
        if (key.SessionId != Start.SessionId)
        {
            return CommandResult.Rejected;
        }

        if (!_journal.TryGet(key, out JournalRecord? record))
        {
            if (requester != key.AuthorId)
            {
                return CommandResult.Rejected;
            }

            _journal.Cancel(key);
            if (requester == Start.AuthorId)
            {
                Broadcast(CommandProtocol.EncodeCancel(key));
            }

            return CommandResult.Cancelled;
        }

        if (record!.IsCancelled)
        {
            return CommandResult.Cancelled;
        }

        if (requester != key.AuthorId && !_model.CanCancel(record.Header))
        {
            return CommandResult.Rejected;
        }

        _journal.Cancel(key);
        _model.Remove(key);
        if (requester == Start.AuthorId)
        {
            Broadcast(CommandProtocol.EncodeCancel(key));
        }

        return CommandResult.Cancelled;
    }

    /// <summary>Applies an authoritative command outcome and updates predicted state.</summary>
    /// <param name="outcome">The server outcome to apply.</param>
    /// <param name="finalPayload">The accepted command payload, or an empty span for non-accepted outcomes.</param>
    /// <returns>The applied outcome, or a failure result if it is incompatible with this session.</returns>
    /// <inheritdoc />
    public CommandOutcome ApplyOutcome(in CommandOutcome outcome, ReadOnlySpan<byte> finalPayload)
    {
        if (outcome.Header.Key.SessionId != Start.SessionId)
        {
            return new CommandOutcome(CommandResult.InvalidMessage, outcome.Header);
        }

        if (_snapshotCursor is CommandCursor cursor
            && outcome.Header.Step <= cursor.Step
            && outcome.Header.Order <= cursor.Order)
        {
            return outcome;
        }

        if (_journal.TryGet(outcome.Header.Key, out JournalRecord? previous) && previous!.IsCancelled)
        {
            return new CommandOutcome(CommandResult.Cancelled, previous.Header);
        }

        if (previous is not null
            && previous.Result == outcome.Result
            && previous.Header == outcome.Header
            && previous.FinalPayload.Span.SequenceEqual(finalPayload))
        {
            return outcome;
        }

        if (outcome.Result == CommandResult.Accepted)
        {
            if (!_commands.TryGet(outcome.Header.TypeId, out ICommandRegistration? registration))
            {
                return new CommandOutcome(CommandResult.UnknownType, outcome.Header);
            }

            var visitor = new OutcomeVisitor(this, outcome.Header, finalPayload.ToArray());
            registration!.Visit(ref visitor);
            ReadOnlySpan<byte> requestPayload = previous is null ? finalPayload : previous.RequestPayload.Span;
            CommandHeader requestHeader = previous is null ? outcome.Header : previous.RequestHeader;
            JournalRecord replacement = new(
                outcome.Header,
                CommandResult.Accepted,
                requestPayload,
                finalPayload,
                requestHeader);
            if (previous is null)
            {
                _journal.Append(replacement);
            }
            else
            {
                _journal.Replace(replacement);
            }

            _model.SetCommand(visitor.Entry ?? throw new InvalidOperationException("Registered command did not create an execution entry."));
            ObserveOrder(outcome.Header.Order);
        }
        else
        {
            if (previous is not null)
            {
                _journal.Replace(new JournalRecord(
                    previous.Header,
                    outcome.Result,
                    previous.RequestPayload.Span,
                    [],
                    previous.RequestHeader));
            }
            else if (outcome.Result == CommandResult.Cancelled)
            {
                _journal.Cancel(outcome.Header.Key);
            }
            else
            {
                _journal.Append(new JournalRecord(outcome.Header, outcome.Result, [], [], outcome.Header));
            }

            _model.Remove(outcome.Header.Key);
        }

        return outcome;
    }

    /// <inheritdoc />
    public void Tick(long simulationStep)
    {
        if (simulationStep < _currentStep)
        {
            throw new ArgumentOutOfRangeException(nameof(simulationStep), "Session steps must not move backwards.");
        }

        _model.Tick(simulationStep);
        _currentStep = simulationStep;
    }

    /// <summary>
    /// Captures a replayable session baseline and the command preparation state.
    /// </summary>
    /// <returns>A snapshot containing session identity, replay cursor, preparation state and model bytes.</returns>
    public SessionSnapshot CaptureSnapshot()
    {
        _model.Tick(_currentStep);
        var writer = new ArrayBufferWriter<byte>();
        long snapshotStep = _model.SaveReplayAnchor(writer);
        var cursor = new CommandCursor(snapshotStep, _nextOrder - 1);
        return new SessionSnapshot(Start.SessionId, Start.ProtocolId, snapshotStep, cursor, _preparation.Capture(), writer.WrittenSpan);
    }

    /// <summary>
    /// Restores this session from a compatible snapshot and discards prior command history.
    /// </summary>
    /// <param name="snapshot">The snapshot to restore.</param>
    /// <exception cref="ArgumentException">The snapshot belongs to another session or protocol.</exception>
    public void Restore(SessionSnapshot snapshot)
    {
        Guard.ThrowIfNull(snapshot, nameof(snapshot));
        if (snapshot.SessionId != Start.SessionId)
        {
            throw new ArgumentException("Snapshot session ID does not match this session.", nameof(snapshot));
        }

        if (snapshot.ProtocolId != Start.ProtocolId)
        {
            throw new ArgumentException("Snapshot protocol ID does not match this session.", nameof(snapshot));
        }

        _model.Load(snapshot.ModelState.Span);
        _journal.Clear();
        _currentStep = snapshot.Step;
        _snapshotCursor = snapshot.Cursor;
        _nextOrder = checked(snapshot.Cursor.Order + 1);
        _preparation.Restore(snapshot.Preparation);
    }

    /// <summary>Dispatches a registered command type ID to a visitor.</summary>
    /// <typeparam name="TVisitor">The visitor type to dispatch.</typeparam>
    /// <param name="typeId">The registered command type ID.</param>
    /// <param name="visitor">The visitor that handles the registered command type.</param>
    /// <exception cref="KeyNotFoundException">The command type ID is not registered.</exception>
    /// <inheritdoc />
    public void VisitCommandType<TVisitor>(ulong typeId, ref TVisitor visitor)
        where TVisitor : ICommandVisitor
        => _commands.Visit(typeId, ref visitor);

    /// <summary>Reads accepted, non-cancelled command records after a snapshot cursor.</summary>
    /// <param name="cursor">The snapshot cursor that defines which accepted records have already been applied.</param>
    /// <returns>Accepted records after the cursor, in deterministic simulation order.</returns>
    /// <inheritdoc />
    public IEnumerable<JournalRecord> ReadAcceptedAfter(CommandCursor cursor)
        => _journal.ReadAcceptedAfter(cursor);

    private byte[] Serialize<T>(in T payload)
    {
        var writer = new ArrayBufferWriter<byte>();
        _payloadHandler.Write(payload, writer);
        return writer.WrittenSpan.ToArray();
    }

    private uint GetNextOrder()
    {
        uint order = _nextOrder;
        _nextOrder = checked(_nextOrder + 1);
        return order;
    }

    private void ObserveOrder(uint order)
    {
        if (order >= _nextOrder)
        {
            _nextOrder = checked(order + 1);
        }
    }

    internal bool TryGetJournalRecord(CommandKey key, out JournalRecord? record) => _journal.TryGet(key, out record);

    internal void SendTo(ulong connectionId, ReadOnlySpan<byte> message) => _transport?.Send(connectionId, message);

    private void Broadcast(ReadOnlySpan<byte> message)
    {
        if (_transport is null)
        {
            return;
        }

        foreach (ulong connectionId in _connections)
        {
            _transport.Send(connectionId, message);
        }
    }

    private CommandOutcome Receive<T>(CommandHeader header, CommandHeader requestHeader, ReadOnlySpan<byte> requestPayload)
    {
        T payload;
        try
        {
            payload = _payloadHandler.Read<T>(requestPayload);
        }
        catch (ArgumentException)
        {
            _journal.Append(new JournalRecord(header, CommandResult.InvalidMessage, requestPayload, [], requestHeader));
            return new CommandOutcome(CommandResult.InvalidMessage, header);
        }

        return Accept(header, requestHeader, requestPayload, payload);
    }

    private CommandOutcome Accept<T>(CommandHeader header, CommandHeader requestHeader, ReadOnlySpan<byte> requestPayload, T payload)
    {
        ICommandPolicy<T> policy = GetPolicy<T>();
        var request = new Command<T>(header, payload);
        if (!ValidateCommon(in request) || !policy.Validate(in request))
        {
            _journal.Append(new JournalRecord(header, CommandResult.Rejected, requestPayload, [], requestHeader));
            return new CommandOutcome(CommandResult.Rejected, header);
        }

        long step = header.Step;
        if (!_model.TrySchedule(ref step))
        {
            var rejected = new CommandHeader(header.Key, header.TypeId, step, header.Order);
            _journal.Append(new JournalRecord(rejected, CommandResult.Rejected, requestPayload, [], requestHeader));
            return new CommandOutcome(CommandResult.Rejected, rejected);
        }

        header = new CommandHeader(header.Key, header.TypeId, step, GetNextOrder());
        CommandPreparation temporary = _preparation.Fork();
        policy.Mutate(ref payload, temporary);
        byte[] finalPayload = Serialize(payload);
        CommandEntry entry = policy.CreateEntry(header, finalPayload, _payloadHandler);
        var record = new JournalRecord(header, CommandResult.Accepted, requestPayload, finalPayload, requestHeader);
        _journal.Append(record);
        _preparation.Restore(temporary.Capture());
        _model.SetCommand(entry);
        return new CommandOutcome(CommandResult.Accepted, header);
    }

    private ICommandPolicy<T> GetPolicy<T>()
        => _policies.TryGetValue(typeof(T), out ICommandPolicy? policy) && policy is ICommandPolicy<T> typedPolicy
            ? typedPolicy
            : new CommandPolicy<T>();

    private bool IsPredicted(ulong typeId)
        => _commands.TryGet(typeId, out ICommandRegistration? registration) && registration is { IsPredicted: true };

    private bool ValidateCommon<T>(in Command<T> command)
    {
        for (int index = 0; index < _validators.Count; index++)
        {
            if (!_validators[index].Validate(in command))
            {
                return false;
            }
        }

        return true;
    }

    private interface ICommandPolicy
    {
        CommandEntry CreateEntry(CommandHeader header, ReadOnlySpan<byte> payload, ICommandPayloadHandler payloadHandler);
    }

    private interface ICommandPolicy<T> : ICommandPolicy
    {
        bool Validate(in Command<T> command);

        void Mutate(ref T payload, CommandPreparation preparation);

        new CommandEntry CreateEntry(CommandHeader header, ReadOnlySpan<byte> payload, ICommandPayloadHandler payloadHandler);
    }

    private sealed class CommandPolicy<T> : ICommandPolicy<T>
    {
        private ICommandValidator<T>? _validator;
        private ICommandMutator<T>? _mutator;
        private ICommandExecutor<T>? _executor;

        public void Register(ICommandValidator<T> validator) => _validator = validator;

        public void Register(ICommandMutator<T> mutator) => _mutator = mutator;

        public void Register(ICommandExecutor<T> executor) => _executor = executor;

        public bool Validate(in Command<T> command) => _validator?.Validate(in command) ?? true;

        public void Mutate(ref T payload, CommandPreparation preparation) => _mutator?.Mutate(ref payload, preparation);

        public CommandEntry CreateEntry(CommandHeader header, ReadOnlySpan<byte> payload, ICommandPayloadHandler payloadHandler)
            => new(header, payload, new CommandEntryInvoker<T>(payloadHandler, _executor));
    }

    private sealed class CommandEntryInvoker<T>(ICommandPayloadHandler payloadHandler, ICommandExecutor<T>? executor) : ICommandEntryInvoker
    {
        public void Execute(CommandHeader header, ReadOnlySpan<byte> finalPayload)
        {
            if (executor is null)
            {
                return;
            }

            T value = payloadHandler.Read<T>(finalPayload);
            var command = new Command<T>(header, value);
            executor.Execute(in command);
        }
    }

    private struct ReceiveVisitor(SessionHost host, CommandHeader header, CommandHeader requestHeader, byte[] payload) : ICommandVisitor
    {
        public CommandOutcome Outcome { get; private set; }

        public void Visit<T>() => Outcome = host.Receive<T>(header, requestHeader, payload);
    }

    private struct OutcomeVisitor(SessionHost host, CommandHeader header, byte[] payload) : ICommandVisitor
    {
        public CommandEntry? Entry { get; private set; }

        public void Visit<T>() => Entry = host.GetPolicy<T>().CreateEntry(header, payload, host._payloadHandler);
    }
}

/// <summary>Hosts sessions and binds authenticated transport connections to their authors.</summary>
public interface ISessionServer
{
    /// <summary>Adds a session to the server's lookup table.</summary>
    /// <param name="session">The session to host.</param>
    void Add(SessionHost session);

    /// <summary>Looks up a session by its numeric session ID.</summary>
    /// <param name="sessionId">The session ID to find.</param>
    /// <param name="session">Receives the session when found; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if the session exists.</returns>
    bool TryGet(ulong sessionId, out SessionHost? session);

    /// <summary>Binds an authenticated connection to a session and author.</summary>
    /// <param name="connectionId">The transport-specific connection identifier.</param>
    /// <param name="session">The session the connection will join.</param>
    /// <param name="author">The author assigned by the server after authentication.</param>
    void Bind(ulong connectionId, SessionHost session, AuthorId author);

    /// <summary>Removes a connection binding and detaches it from its session.</summary>
    /// <param name="connectionId">The transport-specific connection identifier.</param>
    /// <returns><see langword="true"/> if a binding was removed.</returns>
    bool Unbind(ulong connectionId);

    /// <summary>Removes a session and unbinds its connected clients.</summary>
    /// <param name="sessionId">The ID of the session to remove.</param>
    void Remove(ulong sessionId);

    /// <summary>Processes a framed client message using the author bound to its connection.</summary>
    /// <param name="connectionId">The authenticated transport connection.</param>
    /// <param name="message">The encoded proposal or cancellation message.</param>
    /// <returns>The command result, or <see cref="CommandResult.InvalidMessage"/> for malformed data.</returns>
    CommandResult Receive(ulong connectionId, ReadOnlySpan<byte> message);
}

/// <summary>Routes authenticated transport connections to authoritative sessions.</summary>
public sealed class SessionServer : ISessionServer
{
    private readonly Dictionary<ulong, SessionHost> _sessions = [];
    private readonly Dictionary<ulong, (SessionHost Session, AuthorId Author)> _connections = [];
    private readonly ITransport? _transport;

    /// <summary>Creates a server router with an optional transport for replies and broadcasts.</summary>
    /// <param name="transport">The transport used to send messages, or <see langword="null"/> for a sendless router.</param>
    public SessionServer(ITransport? transport = null) => _transport = transport;

    /// <inheritdoc />
    public void Add(SessionHost session)
    {
        Guard.ThrowIfNull(session, nameof(session));
        if (_transport is not null && session.Mode == SessionMode.Server)
        {
            session.AttachTransport(_transport);
        }

        _sessions.Add(session.Start.SessionId.Value, session);
    }

    /// <inheritdoc />
    public bool TryGet(ulong sessionId, out SessionHost? session)
        => _sessions.TryGetValue(sessionId, out session);

    /// <summary>Binds an authenticated connection to a session and author.</summary>
    /// <param name="connectionId">The transport-specific connection identifier.</param>
    /// <param name="session">The session the connection will join.</param>
    /// <param name="author">The author assigned by the server after authentication.</param>
    public void Bind(ulong connectionId, SessionHost session, AuthorId author)
    {
        Guard.ThrowIfNull(session, nameof(session));
        _connections[connectionId] = (session, author);
        session.AttachConnection(connectionId);
    }

    /// <summary>Removes a connection binding and detaches it from its session.</summary>
    /// <param name="connectionId">The transport-specific connection identifier.</param>
    /// <returns><see langword="true"/> if a binding was removed.</returns>
    public bool Unbind(ulong connectionId)
    {
        if (!_connections.Remove(connectionId, out (SessionHost Session, AuthorId Author) binding))
        {
            return false;
        }

        binding.Session.DetachConnection(connectionId);
        return true;
    }

    /// <inheritdoc />
    public void Remove(ulong sessionId)
    {
        _sessions.Remove(sessionId);
        List<ulong> removedConnections = [];
        foreach (KeyValuePair<ulong, (SessionHost Session, AuthorId Author)> connection in _connections)
        {
            if (connection.Value.Session.Start.SessionId.Value == sessionId)
            {
                removedConnections.Add(connection.Key);
            }
        }

        for (int index = 0; index < removedConnections.Count; index++)
        {
            Unbind(removedConnections[index]);
        }
    }

    /// <inheritdoc />
    public CommandResult Receive(ulong connectionId, ReadOnlySpan<byte> message)
    {
        if (!_connections.TryGetValue(connectionId, out (SessionHost Session, AuthorId Author) binding))
        {
            return CommandResult.Rejected;
        }

        if (CommandProtocol.TryReadCommand(message, out CommandHeader header, out ReadOnlySpan<byte> payload))
        {
            CommandOutcome outcome = binding.Session.Receive(binding.Author, header, payload);
            ReadOnlySpan<byte> finalPayload = outcome.Result == CommandResult.Accepted
                && binding.Session.TryGetJournalRecord(outcome.Header.Key, out JournalRecord? record)
                    ? record!.FinalPayload.Span
                    : [];
            byte[] response = CommandProtocol.EncodeOutcome(outcome, finalPayload);
            Broadcast(binding.Session, connectionId, response, outcome.Result == CommandResult.Accepted);
            return outcome.Result;
        }

        if (CommandProtocol.TryReadCancel(message, out CommandKey key))
        {
            CommandResult result = binding.Session.Cancel(binding.Author, key);
            var cancelHeader = new CommandHeader(key, 0, 0, 0);
            byte[] response = CommandProtocol.EncodeOutcome(new CommandOutcome(result, cancelHeader), []);
            Broadcast(binding.Session, connectionId, response, result == CommandResult.Cancelled);
            return result;
        }

        return CommandResult.InvalidMessage;
    }

    /// <summary>Processes an already-decoded command proposal for a bound connection.</summary>
    /// <param name="connectionId">The authenticated transport connection.</param>
    /// <param name="header">The client-supplied proposal header.</param>
    /// <param name="payload">The serialized command payload.</param>
    /// <returns>The authoritative outcome, or a rejection when the connection is not bound.</returns>
    public CommandOutcome Receive(ulong connectionId, in CommandHeader header, ReadOnlySpan<byte> payload)
    {
        if (!_connections.TryGetValue(connectionId, out (SessionHost Session, AuthorId Author) binding))
        {
            return new CommandOutcome(CommandResult.Rejected, header);
        }

        return binding.Session.Receive(binding.Author, header, payload);
    }

    private void Broadcast(SessionHost session, ulong sourceConnection, ReadOnlySpan<byte> message, bool allConnections)
    {
        if (_transport is null)
        {
            session.SendTo(sourceConnection, message);
            return;
        }

        foreach (KeyValuePair<ulong, (SessionHost Session, AuthorId Author)> connection in _connections)
        {
            if (!ReferenceEquals(connection.Value.Session, session)
                || (!allConnections && connection.Key != sourceConnection))
            {
                continue;
            }

            _transport.Send(connection.Key, message);
        }
    }
}
