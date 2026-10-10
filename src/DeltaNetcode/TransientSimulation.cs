namespace Delta.Netcode;

/// <summary>Extends a fixed-step simulation with simulation-only transient updates.</summary>
/// <typeparam name="TInput">The input sample consumed by a transient update.</typeparam>
/// <remarks>
/// The application calls transient updates from its frame loop. Implementations run only the
/// simulation portion and do not advance the session step. The application restores the last
/// fixed-step state with <see cref="ISimulation.Load(System.ReadOnlySpan{byte})"/> before the next
/// fixed session tick.
/// </remarks>
public interface ITransientSimulation<TInput> : ISimulation
{
    /// <summary>Advances the simulation using an uncommitted input sample and elapsed time.</summary>
    /// <param name="input">The current local input.</param>
    /// <param name="deltaTimeSeconds">The elapsed time since the preceding transient update.</param>
    void TickTransient(in TInput input, double deltaTimeSeconds);
}
