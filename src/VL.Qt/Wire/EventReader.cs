using System;
using System.Buffers.Binary;

namespace VL.Qt.Wire
{
    /// <summary>
    /// Iterates the poll buffer filled by vlqt_scene_poll (QtNative → C#). Record layout, little-endian,
    /// every record padded to <see cref="Protocol.CommandAlign"/> bytes like the commands:
    /// <c>u16 event, u16 flags (0), u32 len, u32 feedbackId, payload</c> with <c>len</c> = bytes after the 8-byte header
    /// (feedbackId + payload, excluding padding). Payloads per event are defined in protocol.h.
    /// Allocation-free; a malformed record stops the iteration and sets <see cref="Error"/>.
    /// </summary>
    internal ref struct EventReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _offset;

        public EventReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _offset = 0;
            Event = 0;
            FeedbackId = 0;
            Payload = default;
            Error = null;
        }

        /// <summary>Event id of the current record (<see cref="Protocol.EventId"/>).</summary>
        public ushort Event { get; private set; }

        /// <summary>Feedback id given in BIND; the record goes to the slot it is bound to.</summary>
        public uint FeedbackId { get; private set; }

        /// <summary>Event-specific payload of the current record.</summary>
        public ReadOnlySpan<byte> Payload { get; private set; }

        /// <summary>Null, or the reason the iteration stopped early (truncated/malformed record).</summary>
        public string? Error { get; private set; }

        /// <summary>Advances to the next record; false at the end of the buffer or on a malformed record.</summary>
        public bool MoveNext()
        {
            if (Error != null || _offset >= _data.Length)
                return false;

            var rest = _data.Slice(_offset);
            if (rest.Length < Protocol.HeaderSize + Protocol.EventFieldsSize)
                return Fail("truncated event header");

            ushort ev = BinaryPrimitives.ReadUInt16LittleEndian(rest);
            // Flags (rest.Slice(2)) are reserved and 0; QtNative never sends anything else.
            uint len = BinaryPrimitives.ReadUInt32LittleEndian(rest.Slice(4));
            if (len < Protocol.EventFieldsSize)
                return Fail("event length smaller than feedbackId");
            if (len > rest.Length - Protocol.HeaderSize)
                return Fail("event length exceeds buffer");

            Event = ev;
            FeedbackId = BinaryPrimitives.ReadUInt32LittleEndian(rest.Slice(Protocol.HeaderSize));
            Payload = rest.Slice(Protocol.HeaderSize + Protocol.EventFieldsSize, (int)len - Protocol.EventFieldsSize);

            int total = (Protocol.HeaderSize + (int)len + (Protocol.CommandAlign - 1)) & ~(Protocol.CommandAlign - 1);
            _offset += Math.Min(total, rest.Length); // tolerate missing padding after the last record
            return true;
        }

        private bool Fail(string reason)
        {
            Error = $"{reason} at byte {_offset}";
            Payload = default;
            return false;
        }

        // --- payload helpers (offsets in bytes) ---

        public float ReadF32(int offset) => BinaryPrimitives.ReadSingleLittleEndian(Payload.Slice(offset));
        public int ReadI32(int offset) => BinaryPrimitives.ReadInt32LittleEndian(Payload.Slice(offset));
        public uint ReadU32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(Payload.Slice(offset));
    }
}
