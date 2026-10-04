namespace Delta.Netcode;

/// <summary>Identifies a session within the application or server.</summary>
/// <param name="Value">The application-assigned session identifier.</param>
public readonly record struct SessionId(ulong Value);

/// <summary>Identifies the command protocol schema expected by a session.</summary>
/// <param name="Value">The application-assigned protocol identifier.</param>
public readonly record struct ProtocolId(ulong Value);

/// <summary>Identifies the author assigned to a session participant.</summary>
/// <param name="Value">The application-assigned author identifier.</param>
public readonly record struct AuthorId(uint Value);

/// <summary>Identifies one command by session, author and author-local sequence.</summary>
/// <param name="SessionId">The session that owns the command.</param>
/// <param name="AuthorId">The author that created the command.</param>
/// <param name="Sequence">The monotonically increasing sequence assigned by that author.</param>
public readonly record struct CommandKey(SessionId SessionId, AuthorId AuthorId, ulong Sequence);

/// <summary>
/// Describes the completed simulation step and highest authoritative command order represented by a snapshot.
/// </summary>
/// <param name="Step">The last completed step in the replay anchor.</param>
/// <param name="Order">The highest authoritative session order observed at capture time.</param>
public readonly record struct CommandCursor(long Step, uint Order);

/// <summary>
/// Captures the next entity identifier and random state used when preparing accepted commands.
/// </summary>
/// <param name="NextId">The next identifier available to an accepted command mutator.</param>
/// <param name="RandomState">The current deterministic seed generator state.</param>
public readonly record struct CommandPreparationState(ulong NextId, ulong RandomState);

/// <summary>
/// Identifies a command in a session. Order is assigned monotonically by the authoritative session.
/// </summary>
/// <param name="Key">The session, author and sequence identity of the command.</param>
/// <param name="TypeId">The registered command type identifier.</param>
/// <param name="Step">The simulation step where the command is scheduled.</param>
/// <param name="Order">The authoritative order used to sequence commands at a step.</param>
public readonly record struct CommandHeader(CommandKey Key, ulong TypeId, long Step, uint Order);

/// <summary>Pairs a command header with its application-defined payload.</summary>
/// <typeparam name="T">The command payload type.</typeparam>
/// <param name="Header">The session identity and scheduling data for the command.</param>
/// <param name="Payload">The typed command payload.</param>
public readonly record struct Command<T>(CommandHeader Header, T Payload);

/// <summary>Defines the identity, starting step and deterministic seed for a session.</summary>
/// <param name="SessionId">The unique identifier of the session.</param>
/// <param name="AuthorId">The author identity assigned to this session host.</param>
/// <param name="ProtocolId">The protocol schema identifier expected by participants.</param>
/// <param name="Step">The initial simulation step.</param>
/// <param name="Seed">The initial deterministic seed.</param>
public readonly record struct SessionStart(
    SessionId SessionId,
    AuthorId AuthorId,
    ProtocolId ProtocolId,
    long Step,
    ulong Seed);

/// <summary>Describes the outcome of command validation, routing or cancellation.</summary>
public enum CommandResult : byte
{
    /// <summary>The command passed authority processing and was accepted.</summary>
    Accepted,
    /// <summary>The command failed validation or could not be scheduled.</summary>
    Rejected,
    /// <summary>The command key was already used for a different request.</summary>
    Conflict,
    /// <summary>The command was cancelled.</summary>
    Cancelled,
    /// <summary>The command type ID is not registered by the session.</summary>
    UnknownType,
    /// <summary>The command message or payload could not be decoded.</summary>
    InvalidMessage
}

/// <summary>Describes the authority's decision for a submitted command.</summary>
/// <param name="Result">The decision or protocol failure result.</param>
/// <param name="Header">The authoritative or rejected command header.</param>
public readonly record struct CommandOutcome(CommandResult Result, CommandHeader Header);

/// <summary>Selects how a session host handles commands and transport messages.</summary>
public enum SessionMode : byte
{
    /// <summary>Processes commands locally without a network authority.</summary>
    Local,
    /// <summary>Submits commands to a server and applies authoritative outcomes.</summary>
    Client,
    /// <summary>Validates and orders commands authoritatively for connected clients.</summary>
    Server
}

/// <summary>Marks a payload type for generated command registration.</summary>
[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class, Inherited = false)]
public sealed class NetCommandAttribute : Attribute
{
    /// <summary>Gets or sets the explicit stable ID; zero requests a name-derived ID.</summary>
    public ulong Id { get; set; }

    /// <summary>Gets or sets whether clients apply this command before server confirmation.</summary>
    public bool Predicted { get; set; }
}

/// <summary>Receives generic callbacks for registered command types.</summary>
public interface ICommandVisitor
{
    /// <summary>Handles a command of the registered payload type.</summary>
    /// <typeparam name="T">The registered command payload type.</typeparam>
    void Visit<T>();
}

/// <summary>Describes one command type registered with a session.</summary>
public interface ICommandRegistration
{
    /// <summary>Gets the stable command type ID.</summary>
    ulong Id { get; }

    /// <summary>Gets whether clients predict this command before authority confirmation.</summary>
    bool IsPredicted { get; }

    /// <summary>Gets the CLR payload type associated with this registration.</summary>
    Type CommandType { get; }

    /// <summary>Dispatches this registration's payload type to a visitor.</summary>
    /// <typeparam name="TVisitor">The visitor type.</typeparam>
    /// <param name="visitor">The visitor receiving the typed callback.</param>
    void Visit<TVisitor>(ref TVisitor visitor) where TVisitor : ICommandVisitor;
}

/// <summary>Registers command payload types and resolves their stable IDs.</summary>
public interface ICommandRegistry
{
    /// <summary>Registers a payload type with an ID and prediction setting.</summary>
    /// <typeparam name="T">The command payload type.</typeparam>
    /// <param name="id">The non-zero stable command ID.</param>
    /// <param name="isPredicted">Whether clients apply the command before confirmation.</param>
    void Register<T>(ulong id, bool isPredicted = false);

    /// <summary>Registers a previously created command registration.</summary>
    /// <param name="registration">The command registration to add.</param>
    void Register(ICommandRegistration registration);

    /// <summary>Gets the ID registered for a payload type.</summary>
    /// <typeparam name="T">The registered command payload type.</typeparam>
    /// <returns>The stable command ID.</returns>
    /// <exception cref="KeyNotFoundException">The payload type is not registered.</exception>
    ulong GetId<T>();

    /// <summary>Looks up a registration by command ID.</summary>
    /// <param name="id">The command ID to find.</param>
    /// <param name="registration">Receives the registration when found; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if the ID is registered.</returns>
    bool TryGet(ulong id, out ICommandRegistration? registration);

    /// <summary>Dispatches a registered command ID to a typed visitor.</summary>
    /// <typeparam name="TVisitor">The visitor type.</typeparam>
    /// <param name="id">The registered command ID.</param>
    /// <param name="visitor">The visitor receiving the typed callback.</param>
    /// <exception cref="KeyNotFoundException">The command ID is not registered.</exception>
    void Visit<TVisitor>(ulong id, ref TVisitor visitor) where TVisitor : ICommandVisitor;
}

/// <summary>Provides a typed command registration, including client prediction metadata.</summary>
/// <typeparam name="T">The command payload type.</typeparam>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
#pragma warning disable CS1591
public sealed class CommandRegistration<T> : ICommandRegistration
{
    public CommandRegistration(ulong id, bool isPredicted = false)
    {
        if (id == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(id), "Command ID 0 is reserved.");
        }

        Id = id;
        IsPredicted = isPredicted;
    }

    public ulong Id { get; }

    public bool IsPredicted { get; }

    public Type CommandType => typeof(T);

    public void Visit<TVisitor>(ref TVisitor visitor) where TVisitor : ICommandVisitor
        => visitor.Visit<T>();
}
#pragma warning restore CS1591
