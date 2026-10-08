# Command wire protocol

`CommandProtocol` encodes the byte messages exchanged by `SessionHost`,
`SessionClient` and `SessionServer`. The application transport supplies message
boundaries: one complete encoded message is passed to each receive call. It
must preserve ordering for the status, snapshot/replay records and completion
messages in a synchronization stream.

Every frame begins with one byte identifying its kind. All multi-byte numeric
fields are little-endian. The protocol does not add a magic value or a separate
wire-version field. `ProtocolId` is application-assigned and checked during
join/resume; use the same value only when peers agree on compatible command
IDs and payload codecs. Command and outcome frames do not carry the
`ProtocolId` themselves.

## Frame kinds

| Kind | Frame | Body |
| ---: | --- | --- |
| `1` | Proposal | Command header followed by serialized payload bytes. |
| `2` | Cancellation | Command key. |
| `3` | Outcome | Command header, one-byte `CommandResult`, then final payload bytes. |
| `4` | Snapshot | Snapshot header followed by opaque model-state bytes. |
| `5` | Sync request | Session and protocol IDs, optional-cursor flags and values, current step. |
| `6` | Sync status | One-byte `SessionSyncStatus`, session ID and protocol ID. |
| `7` | Sync complete | Session ID, optional journal revision and authoritative current step. |

A command key is encoded as a 20-byte tuple: `SessionId` (`UInt64`), `AuthorId`
(`UInt32`) and author-local `Sequence` (`UInt64`). A command header appends
`TypeId` (`UInt64`), requested or authoritative `Step` (`Int64`) and
session-wide `Order` (`UInt32`), for 40 bytes total.

Proposal frames contain the 40-byte header and use the rest of the frame as the
payload. Outcome frames contain the same header, a result byte and the
remaining final payload. The session server sends final payload bytes for an
accepted command; other outcomes have no final payload. Cancellation frames
are exactly 21 bytes including their kind byte.

A snapshot has a 49-byte header containing the session ID, protocol ID,
completed anchor step, cursor order, `CommandPreparationState` (`NextId` and
`RandomState`) and a signed 32-bit model-state length. The opaque model bytes
immediately follow. The cursor's step is the same as the anchor step.

A sync request is 42 bytes. Its flag byte uses bit `0x01` for a journal
revision and `0x02` for the last resolved author sequence; other flag bits are
invalid. The fixed-width optional fields remain in the frame and are ignored
when their flag is clear. A sync status is 18 bytes. A sync completion frame
is 26 bytes and has a one-byte flag indicating whether its fixed-width journal
revision is present.

## Reading and ownership

Use the matching `TryRead...` methods to inspect frames. Command and outcome
readers return payload spans that borrow the input frame; they do not copy the
payload. `TryReadSnapshot` copies model state into owned snapshot storage.
`WriteSnapshot` appends a frame to the supplied `IBufferWriter<byte>`;
`EncodeProposal`, `EncodeOutcome`, `EncodeCancel` and the synchronization
encoders return newly allocated byte arrays.

Frame readers validate the message kind, required size and encoded enum/flag
ranges. They do not authenticate a peer, check the connection's bound author,
verify that a command type is registered, or decode an application payload.
Those checks belong to the session/server and the supplied payload handler.

The protocol does not fragment or compress messages, define a maximum frame
size, add reliability or encryption, or provide socket I/O. The application
must frame messages correctly for its transport and protect the connection as
required by its game.
