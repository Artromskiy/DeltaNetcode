using System.Buffers;
using System.Buffers.Binary;

namespace Delta.Netcode;

public sealed class RollbackSessionModel : ISessionModel
{
    private readonly ISimulation _simulation;
    private readonly int _historyDepth;
    private readonly SortedDictionary<long, List<CommandEntry>> _commandsByStep = [];
    private readonly Dictionary<CommandKey, CommandEntry> _commandsByKey = [];
    private readonly SortedDictionary<long, byte[]> _snapshots = [];
    private long? _dirtyStep;
    private long _currentStep;

    public RollbackSessionModel(ISimulation simulation, long initialStep, int historyDepth)
    {
        _simulation = simulation ?? throw new ArgumentNullException(nameof(simulation));
        Guard.ThrowIfLessThan(historyDepth, 1, nameof(historyDepth));

        _currentStep = checked(initialStep - 1);
        _historyDepth = historyDepth;
        _snapshots.Add(initialStep, SaveSimulation());
    }

    public bool TrySchedule(ref long simulationStep)
        => simulationStep >= GetFirstRetainedStep();

    public bool CanCancel(in CommandHeader header)
        => header.Step >= GetFirstRetainedStep();

    public void SetCommand(CommandEntry command)
    {
        Guard.ThrowIfNull(command, nameof(command));
        RemoveFromStep(command.Header.Key);
        if (!_commandsByStep.TryGetValue(command.Header.Step, out List<CommandEntry>? commands))
        {
            commands = [];
            _commandsByStep.Add(command.Header.Step, commands);
        }

        commands.Add(command);
        commands.Sort(static (left, right) => left.Header.Order.CompareTo(right.Header.Order));
        _commandsByKey[command.Header.Key] = command;
        if (command.Header.Step <= _currentStep)
        {
            _dirtyStep = _dirtyStep is null ? command.Header.Step : Math.Min(_dirtyStep.Value, command.Header.Step);
        }
    }

    public void Remove(CommandKey key)
    {
        RemoveFromStep(key);
    }

    public void Tick(long simulationStep)
    {
        if (_dirtyStep is long dirtyStep)
        {
            ReplayFrom(dirtyStep, simulationStep);
            _dirtyStep = null;
            return;
        }

        while (_currentStep < simulationStep)
        {
            TickNext();
        }
    }

    public void Save(IBufferWriter<byte> output)
    {
        Guard.ThrowIfNull(output, nameof(output));
        Span<byte> stepBytes = output.GetSpan(sizeof(long));
        BinaryPrimitives.WriteInt64LittleEndian(stepBytes, _currentStep);
        output.Advance(sizeof(long));
        _simulation.Save(output);
    }

    public long SaveReplayAnchor(IBufferWriter<byte> output)
    {
        Guard.ThrowIfNull(output, nameof(output));

        using SortedDictionary<long, byte[]>.Enumerator enumerator = _snapshots.GetEnumerator();
        if (!enumerator.MoveNext())
        {
            throw new InvalidOperationException("The session model has no retained rollback state.");
        }

        long firstReplayStep = enumerator.Current.Key;
        long completedStep = checked(firstReplayStep - 1);
        Span<byte> stepBytes = output.GetSpan(sizeof(long));
        BinaryPrimitives.WriteInt64LittleEndian(stepBytes, completedStep);
        output.Advance(sizeof(long));
        ReadOnlySpan<byte> simulationState = enumerator.Current.Value;
        simulationState.CopyTo(output.GetSpan(simulationState.Length));
        output.Advance(simulationState.Length);
        return completedStep;
    }

    public void Load(ReadOnlySpan<byte> state)
    {
        if (state.Length < sizeof(long))
        {
            throw new ArgumentException("Session model state must include the current step.", nameof(state));
        }

        _currentStep = BinaryPrimitives.ReadInt64LittleEndian(state);
        _simulation.Load(state[sizeof(long)..]);
        _commandsByStep.Clear();
        _commandsByKey.Clear();
        _snapshots.Clear();
        _snapshots.Add(checked(_currentStep + 1), SaveSimulation());
        _dirtyStep = null;
    }

    private void ReplayFrom(long firstStep, long throughStep)
    {
        if (!_snapshots.TryGetValue(firstStep, out byte[]? snapshot))
        {
            throw new InvalidOperationException($"Rollback step {firstStep} is outside the retained history.");
        }

        _simulation.Load(snapshot);
        _currentStep = firstStep - 1;
        RemoveSnapshotsFrom(firstStep);
        while (_currentStep < throughStep)
        {
            TickNext();
        }
    }

    private void TickNext()
    {
        long nextStep = checked(_currentStep + 1);
        _snapshots[nextStep] = SaveSimulation();
        if (_commandsByStep.TryGetValue(nextStep, out List<CommandEntry>? commands))
        {
            for (int index = 0; index < commands.Count; index++)
            {
                commands[index].Execute();
            }
        }

        _simulation.Tick(nextStep);
        _currentStep = nextStep;
        TrimHistory();
    }

    private byte[] SaveSimulation()
    {
        var writer = new ArrayBufferWriter<byte>();
        _simulation.Save(writer);
        return writer.WrittenSpan.ToArray();
    }

    private void TrimHistory()
    {
        long firstRetainedStep = _currentStep - _historyDepth + 1;
        while (_snapshots.Count > 0)
        {
            using SortedDictionary<long, byte[]>.Enumerator enumerator = _snapshots.GetEnumerator();
            if (!enumerator.MoveNext() || enumerator.Current.Key >= firstRetainedStep)
            {
                return;
            }

            _snapshots.Remove(enumerator.Current.Key);
        }
    }

    private void RemoveSnapshotsFrom(long firstStep)
    {
        List<long> removedSteps = [];
        foreach (KeyValuePair<long, byte[]> snapshot in _snapshots)
        {
            if (snapshot.Key >= firstStep)
            {
                removedSteps.Add(snapshot.Key);
            }
        }

        for (int index = 0; index < removedSteps.Count; index++)
        {
            _snapshots.Remove(removedSteps[index]);
        }
    }

    private void RemoveFromStep(CommandKey key)
    {
        if (!_commandsByKey.TryGetValue(key, out CommandEntry? existing))
        {
            return;
        }

        if (_commandsByStep.TryGetValue(existing.Header.Step, out List<CommandEntry>? commands))
        {
            commands.Remove(existing);
            if (commands.Count == 0)
            {
                _commandsByStep.Remove(existing.Header.Step);
            }
        }

        _commandsByKey.Remove(key);
        if (existing.Header.Step <= _currentStep)
        {
            _dirtyStep = _dirtyStep is null ? existing.Header.Step : Math.Min(_dirtyStep.Value, existing.Header.Step);
        }
    }

    private long GetFirstRetainedStep()
    {
        using SortedDictionary<long, byte[]>.Enumerator enumerator = _snapshots.GetEnumerator();
        return enumerator.MoveNext()
            ? enumerator.Current.Key
            : checked(_currentStep + 1);
    }
}
