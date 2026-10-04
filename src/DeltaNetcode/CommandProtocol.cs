using System.Buffers.Binary;

namespace Delta.Netcode;

public static class CommandProtocol
{
    private const int CommandHeaderSize = 40;
    private const int CommandPrefixSize = 1 + CommandHeaderSize;
    private const int CancelMessageSize = 21;
    private const byte ProposalKind = 1;
    private const byte CancelKind = 2;
    private const byte OutcomeKind = 3;

    public static byte[] EncodeProposal(in CommandHeader header, ReadOnlySpan<byte> payload)
        => EncodeCommand(ProposalKind, header, payload);

    public static byte[] EncodeOutcome(in CommandOutcome outcome, ReadOnlySpan<byte> finalPayload)
    {
        byte[] message = new byte[CommandPrefixSize + 1 + finalPayload.Length];
        message[0] = OutcomeKind;
        WriteHeader(message.AsSpan(1), outcome.Header);
        message[CommandPrefixSize] = (byte)outcome.Result;
        finalPayload.CopyTo(message.AsSpan(CommandPrefixSize + 1));
        return message;
    }

    public static byte[] EncodeCancel(CommandKey key)
    {
        byte[] message = new byte[CancelMessageSize];
        message[0] = CancelKind;
        WriteKey(message.AsSpan(1), key);
        return message;
    }

    public static bool TryReadCommand(ReadOnlySpan<byte> message, out CommandHeader header, out ReadOnlySpan<byte> payload)
    {
        if (message.Length < CommandPrefixSize || message[0] != ProposalKind)
        {
            header = default;
            payload = [];
            return false;
        }

        header = ReadHeader(message[1..]);
        payload = message[CommandPrefixSize..];
        return true;
    }

    public static bool TryReadOutcome(
        ReadOnlySpan<byte> message,
        out CommandOutcome outcome,
        out ReadOnlySpan<byte> finalPayload)
    {
        if (message.Length < CommandPrefixSize + 1 || message[0] != OutcomeKind)
        {
            outcome = default;
            finalPayload = [];
            return false;
        }

        CommandHeader header = ReadHeader(message[1..]);
        byte result = message[CommandPrefixSize];
        if (result > (byte)CommandResult.InvalidMessage)
        {
            outcome = default;
            finalPayload = [];
            return false;
        }

        outcome = new CommandOutcome((CommandResult)result, header);
        finalPayload = message[(CommandPrefixSize + 1)..];
        return true;
    }

    public static bool TryReadCancel(ReadOnlySpan<byte> message, out CommandKey key)
    {
        if (message.Length != CancelMessageSize || message[0] != CancelKind)
        {
            key = default;
            return false;
        }

        key = ReadKey(message[1..]);
        return true;
    }

    private static byte[] EncodeCommand(byte kind, in CommandHeader header, ReadOnlySpan<byte> payload)
    {
        byte[] message = new byte[CommandPrefixSize + payload.Length];
        message[0] = kind;
        WriteHeader(message.AsSpan(1), header);
        payload.CopyTo(message.AsSpan(CommandPrefixSize));
        return message;
    }

    private static void WriteHeader(Span<byte> destination, in CommandHeader header)
    {
        WriteKey(destination, header.Key);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[20..], header.TypeId);
        BinaryPrimitives.WriteInt64LittleEndian(destination[28..], header.Step);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[36..], header.Order);
    }

    private static CommandHeader ReadHeader(ReadOnlySpan<byte> source)
    {
        CommandKey key = ReadKey(source);
        ulong typeId = BinaryPrimitives.ReadUInt64LittleEndian(source[20..]);
        long step = BinaryPrimitives.ReadInt64LittleEndian(source[28..]);
        uint order = BinaryPrimitives.ReadUInt32LittleEndian(source[36..]);
        return new CommandHeader(key, typeId, step, order);
    }

    private static void WriteKey(Span<byte> destination, in CommandKey key)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(destination, key.SessionId.Value);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], key.AuthorId.Value);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[12..], key.Sequence);
    }

    private static CommandKey ReadKey(ReadOnlySpan<byte> source)
    {
        ulong sessionId = BinaryPrimitives.ReadUInt64LittleEndian(source);
        uint authorId = BinaryPrimitives.ReadUInt32LittleEndian(source[8..]);
        ulong sequence = BinaryPrimitives.ReadUInt64LittleEndian(source[12..]);
        return new CommandKey(new SessionId(sessionId), new AuthorId(authorId), sequence);
    }
}
