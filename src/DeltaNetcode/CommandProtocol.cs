using System.Buffers;
using System.Buffers.Binary;

namespace Delta.Netcode;

/// <summary>Encodes and decodes command, snapshot and session synchronization frames.</summary>
public static class CommandProtocol
{
    private const int CommandHeaderSize = 40;
    private const int CommandPrefixSize = 1 + CommandHeaderSize;
    private const int CancelMessageSize = 21;
    private const int SnapshotHeaderSize = 49;
    private const byte ProposalKind = 1;
    private const byte CancelKind = 2;
    private const byte OutcomeKind = 3;
    private const byte SnapshotKind = 4;
    private const byte SyncRequestKind = 5;
    private const byte SyncStatusKind = 6;
    private const byte SyncCompleteKind = 7;
    private const int SyncRequestSize = 1 + 8 + 8 + 1 + 8 + 8 + 8;
    private const int SyncStatusSize = 1 + 1 + 8 + 8;
    private const int SyncCompleteSize = 1 + 8 + 1 + 8 + 8;

    /// <summary>Encodes a request to join or resume an authenticated session connection.</summary>
    /// <param name="request">The requested session, protocol and client cursor.</param>
    /// <returns>A newly allocated synchronization request frame.</returns>
    public static byte[] EncodeSyncRequest(in SessionSyncRequest request)
    {
        byte[] message = new byte[SyncRequestSize];
        message[0] = SyncRequestKind;
        BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(1), request.SessionId.Value);
        BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(9), request.ProtocolId.Value);
        byte flags = 0;
        if (request.Cursor.JournalRevision is not null)
        {
            flags |= 1;
            BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(18), request.Cursor.JournalRevision.Value);
        }

        if (request.Cursor.LastResolvedAuthorSequence is not null)
        {
            flags |= 2;
            BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(26), request.Cursor.LastResolvedAuthorSequence.Value);
        }

        message[17] = flags;
        BinaryPrimitives.WriteInt64LittleEndian(message.AsSpan(34), request.Cursor.CurrentStep);
        return message;
    }

    /// <summary>Reads a session synchronization request.</summary>
    /// <param name="message">The encoded message to inspect.</param>
    /// <param name="request">Receives the decoded request when valid.</param>
    /// <returns><see langword="true"/> if the message is a valid synchronization request.</returns>
    public static bool TryReadSyncRequest(ReadOnlySpan<byte> message, out SessionSyncRequest request)
    {
        if (message.Length != SyncRequestSize || message[0] != SyncRequestKind || (message[17] & ~3) != 0)
        {
            request = default;
            return false;
        }

        byte flags = message[17];
        ulong? revision = (flags & 1) != 0 ? BinaryPrimitives.ReadUInt64LittleEndian(message[18..]) : null;
        ulong? sequence = (flags & 2) != 0 ? BinaryPrimitives.ReadUInt64LittleEndian(message[26..]) : null;
        long currentStep = BinaryPrimitives.ReadInt64LittleEndian(message[34..]);
        request = new SessionSyncRequest(
            new SessionId(BinaryPrimitives.ReadUInt64LittleEndian(message[1..])),
            new ProtocolId(BinaryPrimitives.ReadUInt64LittleEndian(message[9..])),
            new SessionResumeCursor(currentStep, revision, sequence));
        return true;
    }

    /// <summary>Encodes the server's synchronization mode or rejection.</summary>
    /// <param name="status">The result selected for this request.</param>
    /// <param name="sessionId">The session identity associated with the response.</param>
    /// <param name="protocolId">The server's protocol identity for the response.</param>
    /// <returns>A newly allocated synchronization status frame.</returns>
    public static byte[] EncodeSyncStatus(SessionSyncStatus status, SessionId sessionId, ProtocolId protocolId)
    {
        byte[] message = new byte[SyncStatusSize];
        message[0] = SyncStatusKind;
        message[1] = (byte)status;
        BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(2), sessionId.Value);
        BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(10), protocolId.Value);
        return message;
    }

    /// <summary>Reads a synchronization status frame.</summary>
    /// <param name="message">The encoded message to inspect.</param>
    /// <param name="status">Receives the selected synchronization mode or rejection.</param>
    /// <param name="sessionId">Receives the session identity associated with the response.</param>
    /// <param name="protocolId">Receives the server protocol identity.</param>
    /// <returns><see langword="true"/> if the message is a valid synchronization status.</returns>
    public static bool TryReadSyncStatus(
        ReadOnlySpan<byte> message,
        out SessionSyncStatus status,
        out SessionId sessionId,
        out ProtocolId protocolId)
    {
        if (message.Length != SyncStatusSize || message[0] != SyncStatusKind
            || message[1] > (byte)SessionSyncStatus.ProtocolMismatch)
        {
            status = default;
            sessionId = default;
            protocolId = default;
            return false;
        }

        status = (SessionSyncStatus)message[1];
        sessionId = new SessionId(BinaryPrimitives.ReadUInt64LittleEndian(message[2..]));
        protocolId = new ProtocolId(BinaryPrimitives.ReadUInt64LittleEndian(message[10..]));
        return true;
    }

    /// <summary>Encodes completion of a synchronization stream.</summary>
    /// <param name="sessionId">The session that has been synchronized.</param>
    /// <param name="journalRevision">The revision covered by the stream, or <see langword="null"/> when unavailable.</param>
    /// <param name="currentStep">The authoritative simulation step at synchronization completion.</param>
    /// <returns>A newly allocated synchronization completion frame.</returns>
    public static byte[] EncodeSyncComplete(SessionId sessionId, ulong? journalRevision, long currentStep)
    {
        byte[] message = new byte[SyncCompleteSize];
        message[0] = SyncCompleteKind;
        BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(1), sessionId.Value);
        message[9] = journalRevision is null ? (byte)0 : (byte)1;
        if (journalRevision is ulong revision)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(10), revision);
        }

        BinaryPrimitives.WriteInt64LittleEndian(message.AsSpan(18), currentStep);
        return message;
    }

    /// <summary>Reads completion of a synchronization stream.</summary>
    /// <param name="message">The encoded message to inspect.</param>
    /// <param name="sessionId">Receives the synchronized session identity.</param>
    /// <param name="journalRevision">Receives the covered revision, or <see langword="null"/> when unavailable.</param>
    /// <param name="currentStep">Receives the authoritative simulation step.</param>
    /// <returns><see langword="true"/> if the message is a valid synchronization completion frame.</returns>
    public static bool TryReadSyncComplete(
        ReadOnlySpan<byte> message,
        out SessionId sessionId,
        out ulong? journalRevision,
        out long currentStep)
    {
        if (message.Length != SyncCompleteSize || message[0] != SyncCompleteKind || message[9] > 1)
        {
            sessionId = default;
            journalRevision = null;
            currentStep = default;
            return false;
        }

        sessionId = new SessionId(BinaryPrimitives.ReadUInt64LittleEndian(message[1..]));
        journalRevision = message[9] == 0 ? null : BinaryPrimitives.ReadUInt64LittleEndian(message[10..]);
        currentStep = BinaryPrimitives.ReadInt64LittleEndian(message[18..]);
        return true;
    }

    /// <summary>Encodes a client command proposal.</summary>
    /// <param name="header">The proposal header.</param>
    /// <param name="payload">The serialized command payload.</param>
    /// <returns>A newly allocated proposal frame.</returns>
    public static byte[] EncodeProposal(in CommandHeader header, ReadOnlySpan<byte> payload)
        => EncodeCommand(ProposalKind, header, payload);

    /// <summary>Encodes an authoritative command outcome.</summary>
    /// <param name="outcome">The decision and authoritative command header.</param>
    /// <param name="finalPayload">The final payload bytes for an accepted command; otherwise empty.</param>
    /// <returns>A newly allocated outcome frame.</returns>
    public static byte[] EncodeOutcome(in CommandOutcome outcome, ReadOnlySpan<byte> finalPayload)
    {
        byte[] message = new byte[CommandPrefixSize + 1 + finalPayload.Length];
        message[0] = OutcomeKind;
        WriteHeader(message.AsSpan(1), outcome.Header);
        message[CommandPrefixSize] = (byte)outcome.Result;
        finalPayload.CopyTo(message.AsSpan(CommandPrefixSize + 1));
        return message;
    }

    /// <summary>Encodes a cancellation request for a command key.</summary>
    /// <param name="key">The command key to cancel.</param>
    /// <returns>A newly allocated cancellation frame.</returns>
    public static byte[] EncodeCancel(CommandKey key)
    {
        byte[] message = new byte[CancelMessageSize];
        message[0] = CancelKind;
        WriteKey(message.AsSpan(1), key);
        return message;
    }

    /// <summary>
    /// Writes a session snapshot frame to the supplied buffer writer.
    /// </summary>
    /// <param name="snapshot">The snapshot to encode.</param>
    /// <param name="output">The destination buffer writer.</param>
    public static void WriteSnapshot(SessionSnapshot snapshot, IBufferWriter<byte> output)
    {
        Guard.ThrowIfNull(snapshot, nameof(snapshot));
        Guard.ThrowIfNull(output, nameof(output));

        ReadOnlySpan<byte> modelState = snapshot.ModelState.Span;
        int messageSize = checked(SnapshotHeaderSize + modelState.Length);
        Span<byte> destination = output.GetSpan(messageSize)[..messageSize];
        destination[0] = SnapshotKind;
        BinaryPrimitives.WriteUInt64LittleEndian(destination[1..], snapshot.SessionId.Value);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[9..], snapshot.ProtocolId.Value);
        BinaryPrimitives.WriteInt64LittleEndian(destination[17..], snapshot.Step);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[25..], snapshot.Cursor.Order);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[29..], snapshot.Preparation.NextId);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[37..], snapshot.Preparation.RandomState);
        BinaryPrimitives.WriteInt32LittleEndian(destination[45..], modelState.Length);
        modelState.CopyTo(destination[SnapshotHeaderSize..]);
        output.Advance(messageSize);
    }

    /// <summary>
    /// Reads a session snapshot frame and copies its model state into an owned snapshot.
    /// </summary>
    /// <param name="message">The encoded message to inspect.</param>
    /// <param name="snapshot">Receives the decoded snapshot when the frame is valid.</param>
    /// <returns><see langword="true"/> if the message is a valid snapshot frame.</returns>
    public static bool TryReadSnapshot(ReadOnlySpan<byte> message, out SessionSnapshot? snapshot)
    {
        if (message.Length < SnapshotHeaderSize || message[0] != SnapshotKind)
        {
            snapshot = null;
            return false;
        }

        int modelStateLength = BinaryPrimitives.ReadInt32LittleEndian(message[45..]);
        if (modelStateLength < 0 || message.Length != SnapshotHeaderSize + modelStateLength)
        {
            snapshot = null;
            return false;
        }

        ulong sessionId = BinaryPrimitives.ReadUInt64LittleEndian(message[1..]);
        ulong protocolId = BinaryPrimitives.ReadUInt64LittleEndian(message[9..]);
        long step = BinaryPrimitives.ReadInt64LittleEndian(message[17..]);
        uint order = BinaryPrimitives.ReadUInt32LittleEndian(message[25..]);
        ulong nextId = BinaryPrimitives.ReadUInt64LittleEndian(message[29..]);
        ulong randomState = BinaryPrimitives.ReadUInt64LittleEndian(message[37..]);
        snapshot = new SessionSnapshot(
            new SessionId(sessionId),
            new ProtocolId(protocolId),
            step,
            new CommandCursor(step, order),
            new CommandPreparationState(nextId, randomState),
            message[SnapshotHeaderSize..]);
        return true;
    }

    /// <summary>Reads a proposal header and borrows its serialized payload from a message.</summary>
    /// <param name="message">The encoded message to inspect.</param>
    /// <param name="header">Receives the decoded command header.</param>
    /// <param name="payload">Receives the payload slice within <paramref name="message"/>.</param>
    /// <returns><see langword="true"/> if the message has a valid proposal frame prefix.</returns>
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

    /// <summary>Reads an authoritative outcome and borrows its final payload from a message.</summary>
    /// <param name="message">The encoded message to inspect.</param>
    /// <param name="outcome">Receives the decoded outcome.</param>
    /// <param name="finalPayload">Receives the final payload slice within <paramref name="message"/>.</param>
    /// <returns><see langword="true"/> if the message contains a valid outcome frame.</returns>
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

    /// <summary>Reads a cancellation request.</summary>
    /// <param name="message">The encoded message to inspect.</param>
    /// <param name="key">Receives the cancelled command key.</param>
    /// <returns><see langword="true"/> if the message is a valid cancellation frame.</returns>
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
