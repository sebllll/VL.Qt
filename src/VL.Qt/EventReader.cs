using System;
using System.Buffers.Binary;

namespace VL.Qt
{
    /// <summary>
    /// Iterates the poll buffer filled by vlqt_window_poll (QtNative → C#). Record layout, little-endian,
    /// every record padded to <see cref="Protocol.CommandAlign"/> bytes like the commands:
    /// <c>u16 event, u16 len, u32 feedbackId, u32 index, payload</c> with <c>len</c> = bytes after the 4-byte header
    /// (feedbackId + index + payload, excluding padding). Payloads per event are defined in plan 06.
    /// Allocation-free; a malformed record stops the iteration and sets <see cref="Error"/>.
    /// </summary>
    internal ref struct EventReader
    {
        public const int RecordFieldsSize = 8; // u32 feedbackId, u32 index

        private readonly ReadOnlySpan<byte> _data;
        private int _offset;

        public EventReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _offset = 0;
            Event = 0;
            FeedbackId = 0;
            Index = 0;
            Payload = default;
            Error = null;
        }

        /// <summary>Event id of the current record (<see cref="Protocol.Ev"/>).</summary>
        public ushort Event { get; private set; }

        /// <summary>Feedback id given in BIND.</summary>
        public uint FeedbackId { get; private set; }

        /// <summary>Instance index within the feedback (e.g. spread slice).</summary>
        public uint Index { get; private set; }

        /// <summary>Event-specific payload of the current record.</summary>
        public ReadOnlySpan<byte> Payload { get; private set; }

        /// <summary>Byte offset of the current record in the buffer.</summary>
        public int RecordOffset { get; private set; }

        /// <summary>Null, or the reason the iteration stopped early (truncated/malformed record).</summary>
        public string? Error { get; private set; }

        /// <summary>Advances to the next record; false at the end of the buffer or on a malformed record.</summary>
        public bool MoveNext()
        {
            if (Error != null || _offset >= _data.Length)
                return false;

            var rest = _data.Slice(_offset);
            if (rest.Length < Protocol.HeaderSize + RecordFieldsSize)
                return Fail("truncated event header");

            ushort ev = BinaryPrimitives.ReadUInt16LittleEndian(rest);
            int len = BinaryPrimitives.ReadUInt16LittleEndian(rest.Slice(2));
            if (len < RecordFieldsSize)
                return Fail("event length smaller than feedbackId + index");
            if (Protocol.HeaderSize + len > rest.Length)
                return Fail("event length exceeds buffer");

            RecordOffset = _offset;
            Event = ev;
            FeedbackId = BinaryPrimitives.ReadUInt32LittleEndian(rest.Slice(4));
            Index = BinaryPrimitives.ReadUInt32LittleEndian(rest.Slice(8));
            Payload = rest.Slice(Protocol.HeaderSize + RecordFieldsSize, len - RecordFieldsSize);

            int total = (Protocol.HeaderSize + len + (Protocol.CommandAlign - 1)) & ~(Protocol.CommandAlign - 1);
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
