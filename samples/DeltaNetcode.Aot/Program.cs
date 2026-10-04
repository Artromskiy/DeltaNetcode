using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Delta.Netcode;

GeneratedSmoke.Run();

[NetCommand]
public struct AddScoreCommand
{
    public int Amount;
}

internal static class GeneratedSmoke
{
    internal static void Run()
    {
        var commands = new CommandRegistry();
        foreach (ICommandRegistration registration in GeneratedCommands.Registrations)
        {
            commands.Register(registration);
        }

        var simulation = new ScoreSimulation();
        var model = new RollbackSessionModel(simulation, initialStep: 0, historyDepth: 8);
        var session = new SessionHost(
            new SessionStart(new SessionId(1), new AuthorId(1), new ProtocolId(1), 0, 123),
            commands,
            new UnmanagedPayloadHandler(),
            model,
            new MemoryCommandJournal());
        session.Register<AddScoreCommand>(commands.GetId<AddScoreCommand>());
        session.Register<AddScoreCommand>(new AddScoreExecutor(simulation));

        session.Send(new AddScoreCommand { Amount = 5 }, simulationStep: 0);
        session.Tick(0);
        if (simulation.Score != 5)
        {
            throw new InvalidOperationException($"Expected score 5, got {simulation.Score}.");
        }
    }

    private sealed class UnmanagedPayloadHandler : ICommandPayloadHandler
    {
        public void Write<T>(in T payload, IBufferWriter<byte> output)
        {
            if (typeof(T) != typeof(AddScoreCommand))
            {
                throw new NotSupportedException($"No AOT smoke codec for {typeof(T)}.");
            }

            Span<byte> destination = output.GetSpan(sizeof(int));
            BinaryPrimitives.WriteInt32LittleEndian(destination, ((AddScoreCommand)(object)payload).Amount);
            output.Advance(sizeof(int));
        }

        public T Read<T>(ReadOnlySpan<byte> payload)
        {
            if (typeof(T) != typeof(AddScoreCommand) || payload.Length != sizeof(int))
            {
                throw new ArgumentException("AOT smoke payload must be a four-byte AddScoreCommand.", nameof(payload));
            }

            int amount = BinaryPrimitives.ReadInt32LittleEndian(payload);
            return (T)(object)new AddScoreCommand { Amount = amount };
        }
    }

    private sealed class ScoreSimulation : ISimulation
    {
        public int Score { get; set; }

        public void Tick(long simulationStep)
        {
        }

        public void Save(IBufferWriter<byte> output)
        {
            Span<byte> destination = output.GetSpan(sizeof(int));
            int score = Score;
            MemoryMarshal.Write(destination, in score);
            output.Advance(sizeof(int));
        }

        public void Load(ReadOnlySpan<byte> state) => Score = MemoryMarshal.Read<int>(state);
    }

    private sealed class AddScoreExecutor(ScoreSimulation simulation) : ICommandExecutor<AddScoreCommand>
    {
        public void Execute(in Command<AddScoreCommand> command) => simulation.Score += command.Payload.Amount;
    }
}
