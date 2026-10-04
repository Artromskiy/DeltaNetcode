using System.Buffers;

namespace Delta.Netcode;

public interface ISession
{
    CommandKey Send<T>(in T payload, long simulationStep) where T : struct;

    CommandResult Cancel(CommandKey key);

    void Tick(long simulationStep);
}

public sealed class SessionHost : ISession
{
    private readonly Dictionary<Type, ICommandPolicy> _policies = [];
    private readonly Dictionary<long, uint> _ordersByStep = [];
    private readonly List<ICommandValidator> _validators = [];
    private readonly ICommandRegistry _commands;
    private readonly ICommandPayloadHandler _payloadHandler;
    private readonly ICommandJournal _journal;
    private readonly ISessionModel _model;
    private readonly CommandPreparation _preparation;
    private readonly ITransport? _transport;
    private readonly HashSet<ulong> _connections = [];
    private ulong _nextSequence;

    public SessionHost(
        SessionStart start,
        ICommandRegistry commands,
        ICommandPayloadHandler payloadHandler,
        ISessionModel model,
        ICommandJournal journal,
        ulong firstSequence = 0,
        ITransport? transport = null)
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
        _nextSequence = firstSequence;
        _preparation = new CommandPreparation(1, start.Seed);
    }

    public SessionStart Start { get; }

    public void AttachConnection(ulong connectionId) => _connections.Add(connectionId);

    public bool DetachConnection(ulong connectionId) => _connections.Remove(connectionId);

    public void AddValidator(ICommandValidator validator)
    {
        Guard.ThrowIfNull(validator, nameof(validator));

        _validators.Add(validator);
    }

    public void Register<T>(
        ulong id,
        ICommandValidator<T>? validator = null,
        ICommandMutator<T>? mutator = null,
        ICommandExecutor<T>? executor = null) where T : struct
    {
        _commands.Register<T>(id);
        _policies[typeof(T)] = new CommandPolicy<T>(validator, mutator, executor);
    }

    public CommandKey Send<T>(in T payload, long simulationStep) where T : struct
    {
        ulong typeId = _commands.GetId<T>();
        var key = new CommandKey(Start.SessionId, Start.AuthorId, checked(_nextSequence++));
        var header = new CommandHeader(key, typeId, simulationStep, GetNextOrder(simulationStep));
        byte[] bytes = Serialize(payload);
        var record = new JournalRecord(header, CommandResult.Accepted, bytes, bytes);
        _journal.Append(record);
        _model.SetCommand(GetPolicy<T>().CreateEntry(header, bytes, _payloadHandler));
        Broadcast(CommandProtocol.EncodeProposal(header, bytes));
        return key;
    }

    public CommandOutcome Receive(AuthorId authenticatedAuthor, in CommandHeader proposal, ReadOnlySpan<byte> requestPayload)
    {
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

    public CommandResult Cancel(CommandKey key) => Cancel(Start.AuthorId, key);

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

    public CommandOutcome ApplyOutcome(in CommandOutcome outcome, ReadOnlySpan<byte> finalPayload)
    {
        if (outcome.Header.Key.SessionId != Start.SessionId)
        {
            return new CommandOutcome(CommandResult.InvalidMessage, outcome.Header);
        }

        if (_journal.TryGet(outcome.Header.Key, out JournalRecord? previous) && previous!.IsCancelled)
        {
            return new CommandOutcome(CommandResult.Cancelled, previous.Header);
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

    public void Tick(long simulationStep)
    {
        _model.Tick(simulationStep);
    }

    private byte[] Serialize<T>(in T payload) where T : struct
    {
        var writer = new ArrayBufferWriter<byte>();
        _payloadHandler.Write(payload, writer);
        return writer.WrittenSpan.ToArray();
    }

    private uint GetNextOrder(long step)
    {
        _ordersByStep.TryGetValue(step, out uint order);
        _ordersByStep[step] = checked(order + 1);
        return order;
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

    private CommandOutcome Receive<T>(CommandHeader header, CommandHeader requestHeader, ReadOnlySpan<byte> requestPayload) where T : struct
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

        header = new CommandHeader(header.Key, header.TypeId, step, GetNextOrder(step));
        CommandPreparation temporary = _preparation.Fork();
        policy.Mutate(ref payload, temporary);
        byte[] finalPayload = Serialize(payload);
        var record = new JournalRecord(header, CommandResult.Accepted, requestPayload, finalPayload, requestHeader);
        _journal.Append(record);
        _preparation.Restore(temporary.Capture());
        _model.SetCommand(policy.CreateEntry(header, finalPayload, _payloadHandler));
        return new CommandOutcome(CommandResult.Accepted, header);
    }

    private ICommandPolicy<T> GetPolicy<T>() where T : struct
        => _policies.TryGetValue(typeof(T), out ICommandPolicy? policy) && policy is ICommandPolicy<T> typedPolicy
            ? typedPolicy
            : new CommandPolicy<T>(null, null, null);

    private bool ValidateCommon<T>(in Command<T> command) where T : struct
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

    private interface ICommandPolicy<T> : ICommandPolicy where T : struct
    {
        bool Validate(in Command<T> command);

        void Mutate(ref T payload, CommandPreparation preparation);

        new CommandEntry CreateEntry(CommandHeader header, ReadOnlySpan<byte> payload, ICommandPayloadHandler payloadHandler);
    }

    private sealed class CommandPolicy<T>(
        ICommandValidator<T>? validator,
        ICommandMutator<T>? mutator,
        ICommandExecutor<T>? executor) : ICommandPolicy<T> where T : struct
    {
        public bool Validate(in Command<T> command) => validator?.Validate(in command) ?? true;

        public void Mutate(ref T payload, CommandPreparation preparation) => mutator?.Mutate(ref payload, preparation);

        public CommandEntry CreateEntry(CommandHeader header, ReadOnlySpan<byte> payload, ICommandPayloadHandler payloadHandler)
            => new(header, payload, new CommandEntryInvoker<T>(payloadHandler, executor));
    }

    private sealed class CommandEntryInvoker<T>(ICommandPayloadHandler payloadHandler, ICommandExecutor<T>? executor) : ICommandEntryInvoker where T : struct
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

        public void Visit<T>() where T : struct => Outcome = host.Receive<T>(header, requestHeader, payload);
    }

    private struct OutcomeVisitor(SessionHost host, CommandHeader header, byte[] payload) : ICommandVisitor
    {
        public CommandEntry? Entry { get; private set; }

        public void Visit<T>() where T : struct => Entry = host.GetPolicy<T>().CreateEntry(header, payload, host._payloadHandler);
    }
}

public interface ISessionServer
{
    void Add(SessionHost session);

    bool TryGet(ulong sessionId, out SessionHost? session);

    void Remove(ulong sessionId);

    CommandResult Receive(ulong connectionId, ReadOnlySpan<byte> message);
}

public sealed class SessionServer : ISessionServer
{
    private readonly Dictionary<ulong, SessionHost> _sessions = [];
    private readonly Dictionary<ulong, (SessionHost Session, AuthorId Author)> _connections = [];
    private readonly ITransport? _transport;

    public SessionServer(ITransport? transport = null) => _transport = transport;

    public void Add(SessionHost session)
    {
        Guard.ThrowIfNull(session, nameof(session));
        _sessions.Add(session.Start.SessionId.Value, session);
    }

    public bool TryGet(ulong sessionId, out SessionHost? session)
        => _sessions.TryGetValue(sessionId, out session);

    public void Bind(ulong connectionId, SessionHost session, AuthorId author)
    {
        Guard.ThrowIfNull(session, nameof(session));
        _connections[connectionId] = (session, author);
        session.AttachConnection(connectionId);
    }

    public bool Unbind(ulong connectionId)
    {
        if (!_connections.Remove(connectionId, out (SessionHost Session, AuthorId Author) binding))
        {
            return false;
        }

        binding.Session.DetachConnection(connectionId);
        return true;
    }

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
