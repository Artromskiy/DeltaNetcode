namespace Delta.Netcode;

public sealed class CommandRegistry : ICommandRegistry
{
    private readonly Dictionary<ulong, ICommandRegistration> _registrationsById = [];
    private readonly Dictionary<Type, ICommandRegistration> _registrationsByType = [];

    public void Register<T>(ulong id, bool isPredicted = false)
        => Register(new CommandRegistration<T>(id, isPredicted));

    public void Register(ICommandRegistration registration)
    {
        Guard.ThrowIfNull(registration, nameof(registration));
        if (registration.Id == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(registration), "Command ID 0 is reserved.");
        }

        if (_registrationsById.TryGetValue(registration.Id, out ICommandRegistration? byId))
        {
            if (byId.CommandType == registration.CommandType)
            {
                if (byId.IsPredicted != registration.IsPredicted)
                {
                    throw new InvalidOperationException($"Command '{registration.CommandType}' is already registered with different prediction metadata.");
                }

                return;
            }

            throw new InvalidOperationException($"Command ID 0x{registration.Id:X16} is already registered to '{byId.CommandType}'.");
        }

        if (_registrationsByType.TryGetValue(registration.CommandType, out ICommandRegistration? byType))
        {
            throw new InvalidOperationException($"Command type '{registration.CommandType}' is already registered with ID 0x{byType.Id:X16}.");
        }

        _registrationsById.Add(registration.Id, registration);
        _registrationsByType.Add(registration.CommandType, registration);
    }

    public ulong GetId<T>()
        => _registrationsByType.TryGetValue(typeof(T), out ICommandRegistration? registration)
            ? registration.Id
            : throw new KeyNotFoundException($"Command type '{typeof(T)}' is not registered.");

    public bool TryGet(ulong id, out ICommandRegistration? registration)
        => _registrationsById.TryGetValue(id, out registration);

    public void Visit<TVisitor>(ulong id, ref TVisitor visitor) where TVisitor : struct, ICommandVisitor
    {
        if (!_registrationsById.TryGetValue(id, out ICommandRegistration? registration))
        {
            throw new KeyNotFoundException($"Command ID 0x{id:X16} is not registered.");
        }

        registration.Visit(ref visitor);
    }
}
