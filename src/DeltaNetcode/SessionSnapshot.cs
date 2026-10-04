namespace Delta.Netcode;

/// <summary>
/// An owned copy of the session identity, simulation state, and command preparation state.
/// </summary>
public sealed class SessionSnapshot
{
    private readonly byte[] _modelState;

    /// <summary>
    /// Creates a snapshot and copies the supplied model state into owned storage.
    /// </summary>
    /// <param name="sessionId">The session represented by the snapshot.</param>
    /// <param name="protocolId">The command protocol represented by the snapshot.</param>
    /// <param name="step">The last completed step in the replay anchor.</param>
    /// <param name="cursor">The replay step and authoritative command-order boundary.</param>
    /// <param name="preparation">The command preparation state at capture time.</param>
    /// <param name="modelState">The replay anchor state written by the session model.</param>
    public SessionSnapshot(
        SessionId sessionId,
        ProtocolId protocolId,
        long step,
        CommandCursor cursor,
        CommandPreparationState preparation,
        ReadOnlySpan<byte> modelState)
    {
        if (cursor.Step != step)
        {
            throw new ArgumentException("The command cursor step must match the snapshot step.", nameof(cursor));
        }

        SessionId = sessionId;
        ProtocolId = protocolId;
        Step = step;
        Cursor = cursor;
        Preparation = preparation;
        _modelState = modelState.ToArray();
    }

    /// <summary>
    /// Gets the identity of the session represented by this snapshot.
    /// </summary>
    public SessionId SessionId { get; }

    /// <summary>
    /// Gets the command protocol expected by this snapshot.
    /// </summary>
    public ProtocolId ProtocolId { get; }

    /// <summary>
    /// Gets the last completed step represented by the model state.
    /// </summary>
    public long Step { get; }

    /// <summary>
    /// Gets the simulation and authoritative-order boundary represented by the snapshot.
    /// </summary>
    public CommandCursor Cursor { get; }

    /// <summary>
    /// Gets the command preparation state at the snapshot's journal boundary.
    /// </summary>
    public CommandPreparationState Preparation { get; }

    /// <summary>
    /// Gets the owned, opaque model state.
    /// </summary>
    public ReadOnlyMemory<byte> ModelState => _modelState;
}
