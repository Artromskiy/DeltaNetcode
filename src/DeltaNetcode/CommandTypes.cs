namespace Delta.Netcode;

public readonly record struct SessionId(ulong Value);

public readonly record struct ProtocolId(ulong Value);

public readonly record struct AuthorId(uint Value);

public readonly record struct CommandKey(SessionId SessionId, AuthorId AuthorId, ulong Sequence);

public readonly record struct CommandHeader(CommandKey Key, ulong TypeId, long Step, uint Order);

public readonly record struct Command<T>(CommandHeader Header, T Payload) where T : struct;

public readonly record struct SessionStart(
    SessionId SessionId,
    AuthorId AuthorId,
    ProtocolId ProtocolId,
    long Step,
    ulong Seed);

public enum CommandResult : byte
{
    Accepted,
    Rejected,
    Conflict,
    Cancelled,
    UnknownType,
    InvalidMessage
}

public readonly record struct CommandOutcome(CommandResult Result, CommandHeader Header);

[AttributeUsage(AttributeTargets.Struct, Inherited = false)]
public sealed class NetCommandAttribute : Attribute
{
    public ulong Id { get; set; }
}

public interface ICommandVisitor
{
    void Visit<T>() where T : struct;
}

public interface ICommandRegistration
{
    ulong Id { get; }

    Type CommandType { get; }

    void Visit<TVisitor>(ref TVisitor visitor) where TVisitor : struct, ICommandVisitor;
}

public interface ICommandRegistry
{
    void Register<T>(ulong id) where T : struct;

    void Register(ICommandRegistration registration);

    ulong GetId<T>() where T : struct;

    bool TryGet(ulong id, out ICommandRegistration? registration);

    void Visit<TVisitor>(ulong id, ref TVisitor visitor) where TVisitor : struct, ICommandVisitor;
}

public sealed class CommandRegistration<T> : ICommandRegistration where T : struct
{
    public CommandRegistration(ulong id)
    {
        if (id == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(id), "Command ID 0 is reserved.");
        }

        Id = id;
    }

    public ulong Id { get; }

    public Type CommandType => typeof(T);

    public void Visit<TVisitor>(ref TVisitor visitor) where TVisitor : struct, ICommandVisitor
        => visitor.Visit<T>();
}
