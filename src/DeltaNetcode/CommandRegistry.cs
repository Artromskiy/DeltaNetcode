namespace Delta.Netcode;

/// <summary>Maps command payload types and stable IDs for one session.</summary>
public sealed class CommandRegistry : ICommandRegistry
{
    private readonly Dictionary<ulong, ICommandRegistration> _registrationsById = [];
    private readonly Dictionary<Type, ICommandRegistration> _registrationsByType = [];

    /// <summary>Registers a payload type with an ID and prediction setting.</summary>
    /// <typeparam name="T">The command payload type.</typeparam>
    /// <param name="id">The non-zero stable command ID.</param>
    /// <param name="isPredicted">Whether clients apply the command before confirmation.</param>
    public void Register<T>(ulong id, bool isPredicted = false)
        => Register(new CommandRegistration<T>(id, isPredicted));

    /// <summary>Adds a command registration to this registry.</summary>
    /// <param name="registration">The registration to add.</param>
    /// <exception cref="ArgumentNullException"><paramref name="registration"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The ID or payload type conflicts with an existing registration.</exception>
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

    /// <summary>Gets the registered ID for a payload type.</summary>
    /// <typeparam name="T">The registered command payload type.</typeparam>
    /// <returns>The stable command ID.</returns>
    /// <exception cref="KeyNotFoundException">The payload type is not registered.</exception>
    public ulong GetId<T>()
        => _registrationsByType.TryGetValue(typeof(T), out ICommandRegistration? registration)
            ? registration.Id
            : throw new KeyNotFoundException($"Command type '{typeof(T)}' is not registered.");

    /// <summary>Looks up a command registration by its ID.</summary>
    /// <param name="id">The command ID to find.</param>
    /// <param name="registration">Receives the registration when found; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if the ID is registered.</returns>
    public bool TryGet(ulong id, out ICommandRegistration? registration)
        => _registrationsById.TryGetValue(id, out registration);

    /// <summary>Dispatches a registered ID to a visitor specialized for its payload type.</summary>
    /// <typeparam name="TVisitor">The visitor type.</typeparam>
    /// <param name="id">The registered command ID.</param>
    /// <param name="visitor">The visitor that receives the typed callback.</param>
    /// <exception cref="KeyNotFoundException">The command ID is not registered.</exception>
    public void Visit<TVisitor>(ulong id, ref TVisitor visitor) where TVisitor : ICommandVisitor
    {
        if (!_registrationsById.TryGetValue(id, out ICommandRegistration? registration))
        {
            throw new KeyNotFoundException($"Command ID 0x{id:X16} is not registered.");
        }

        registration.Visit(ref visitor);
    }
}
