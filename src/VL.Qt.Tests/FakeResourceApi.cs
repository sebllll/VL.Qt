using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Lib.Basics.Imaging;
using VL.Lib.Mathematics;
using VL.Qt.Elements;
using VL.Qt.Images;
using VL.Qt.Interop;
using VL.Qt.Nodes;
using VL.Qt.Reconciliation;
using VL.Qt.Wire;
using Image = VL.Qt.Elements.Image;
using Path = VL.Lib.IO.Path;

namespace VL.Qt.Tests
{
    /// <summary>
    /// The resource API of QtNative without Qt: records calls, hands out ids and status records like vlqt_res_*.
    /// </summary>
    internal sealed class FakeResourceApi : IResourceApi
    {
        private readonly List<byte> _queue = new();
        private uint _next;

        public readonly List<string> Calls = new();
        public readonly List<ulong> Released = new();
        public Native.Result CreateRc, LoadRc, SnapshotRc;
        /// <summary>Size of the last finished snapshot as ReadSnapshot reports it (null = none yet: NoData); its bytes are
        /// <see cref="SnapshotByte"/>.</summary>
        public (int Width, int Height)? SnapshotSize;
        public byte SnapshotByte = 0x7F;
        public int Shutdowns;
        /// <summary>Size of the preview ReadPreview reports (null = none: NoData); its bytes are <see cref="PreviewByte"/>.</summary>
        public (int Width, int Height)? PreviewSize;
        public byte PreviewByte = 0x55;
        public int PreviewCalls;
        /// <summary>Flags of the LoadImage calls and desc.flags of the SetPixels calls.</summary>
        public readonly List<uint> LoadFlags = new();
        public readonly List<int> PixelFlags = new();
        /// <summary>The upload argument (<see cref="Protocol.ImageUpload"/>) of every LoadImage and SetPixels call.</summary>
        public readonly List<int> Uploads = new();
        /// <summary>Records written into the queue when Wait is called (QtNative's synchronous decode + upload).</summary>
        public Action<ulong>? OnWait;
        public Native.vlqt_res_info[] Listed = Array.Empty<Native.vlqt_res_info>();
        public int Polls;

        public Native.Result AudioTapInit(ulong id, int channels, int sampleRate)
        {
            Calls.Add($"audio tap init {id:X} {channels} {sampleRate}");
            return Native.Result.Ok;
        }

        public Native.Result Create(int kind, out ulong id)
        {
            id = 0;
            if (CreateRc != Native.Result.Ok)
                return CreateRc;
            id = ++_next | (7UL << 32); // index | generation
            Calls.Add($"create {kind}");
            return Native.Result.Ok;
        }

        public Native.Result Release(ulong id)
        {
            Released.Add(id);
            Calls.Add($"release {id:X}");
            return Native.Result.Ok;
        }

        public Native.Result Poll(Span<byte> buffer, out int written, out int required)
        {
            Polls++;
            written = 0;
            required = _queue.Count;
            if (_queue.Count > buffer.Length)
                return Native.Result.BufferTooSmall;
            var n = _queue.Count;
            for (int i = 0; i < n; i++)
                buffer[i] = _queue[i];
            _queue.Clear();
            written = n;
            return Native.Result.Ok;
        }

        public int Wait(ulong id, int timeoutMs)
        {
            Calls.Add($"wait {id:X}");
            OnWait?.Invoke(id);
            return Protocol.ResourceState.Ready;
        }

        public Native.Result LoadImage(ulong id, string utf8Path, uint flags, int upload)
        {
            Calls.Add($"load {id:X} {utf8Path} {flags}");
            LoadFlags.Add(flags);
            Uploads.Add(upload);
            return LoadRc;
        }

        public Native.Result LoadShader(ulong id, string utf8Path, int stage)
        {
            Calls.Add($"shader {id:X} {utf8Path} {stage}");
            return LoadRc;
        }

        public Native.Result LoadShaderSource(ulong id, string source, int stage)
        {
            Calls.Add($"source {id:X} {source.Length} {stage}");
            return LoadRc;
        }

        public Native.Result SetPixels(ulong id, in Native.vlqt_pixel_desc desc, ReadOnlySpan<byte> data, int upload)
        {
            Calls.Add($"pixels {id:X} {desc.width}x{desc.height} row {desc.row_bytes} fmt {desc.format} pm {desc.premultiplied} len {data.Length}");
            PixelFlags.Add(desc.flags);
            Uploads.Add(upload);
            return Native.Result.Ok;
        }

        public int List(Span<Native.vlqt_res_info> buffer)
        {
            for (int i = 0; i < Listed.Length && i < buffer.Length; i++)
                buffer[i] = Listed[i];
            return Listed.Length;
        }

        public void Shutdown() => Shutdowns++;

        public Native.Result Snapshot(ulong id, SceneHandle window, uint elementId, float scale)
        {
            Calls.Add($"snapshot {id:X} {elementId} {scale}");
            return SnapshotRc;
        }

        public Native.Result SnapshotFit(ulong id, SceneHandle window, uint elementId, int maxEdgePx)
        {
            Calls.Add($"snapshot_fit {id:X} {elementId} {maxEdgePx}");
            return SnapshotRc;
        }

        public Native.Result ReadSnapshot(ulong id, Span<byte> buffer, out Native.vlqt_pixel_desc desc, out int written, out int required)
        {
            desc = default;
            written = required = 0;
            if (SnapshotSize is not { } size)
                return Native.Result.NoData;
            required = size.Width * size.Height * 4;
            desc = new Native.vlqt_pixel_desc { width = size.Width, height = size.Height, row_bytes = size.Width * 4, format = 9, premultiplied = 1 };
            if (buffer.Length < required)
                return Native.Result.BufferTooSmall;
            buffer[..required].Fill(SnapshotByte);
            written = required;
            return Native.Result.Ok;
        }

        public Native.Result ReadPreview(ulong id, Span<byte> buffer, out Native.vlqt_pixel_desc desc, out int written, out int required)
        {
            PreviewCalls++;
            desc = default;
            written = required = 0;
            if (PreviewSize is not { } size)
                return Native.Result.NoData;
            required = size.Width * size.Height * 4;
            desc = new Native.vlqt_pixel_desc { width = size.Width, height = size.Height, row_bytes = size.Width * 4, format = 9, premultiplied = 1 };
            if (buffer.Length < required)
                return Native.Result.BufferTooSmall;
            buffer[..required].Fill(PreviewByte);
            written = required;
            return Native.Result.Ok;
        }

        private void Record(ushort ev, ulong id, ReadOnlySpan<byte> payload)
        {
            var len = 8 + payload.Length;
            Span<byte> head = stackalloc byte[Protocol.HeaderSize + 8];
            BinaryPrimitives.WriteUInt16LittleEndian(head, ev);
            BinaryPrimitives.WriteUInt32LittleEndian(head[4..], (uint)len);
            BinaryPrimitives.WriteUInt64LittleEndian(head[Protocol.HeaderSize..], id);
            _queue.AddRange(head.ToArray());
            _queue.AddRange(payload.ToArray());
            while (_queue.Count % 4 != 0)
                _queue.Add(0);
        }

        public void State(ulong id, byte state) => Record(Protocol.ResourceEventId.State, id, new[] { state });
        public void Error(ulong id, string text) => Record(Protocol.ResourceEventId.Error, id, Encoding.UTF8.GetBytes(text));
        public void Warning(ulong id, string text) => Record(Protocol.ResourceEventId.Warning, id, Encoding.UTF8.GetBytes(text));

        public void Info(ulong id, int width, int height)
        {
            var p = new byte[20];
            BinaryPrimitives.WriteUInt32LittleEndian(p, (uint)width);
            BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), (uint)height);
            BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(8), 7);
            BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(12), (ulong)(width * height * 4));
            Record(Protocol.ResourceEventId.Info, id, p);
        }

        public void Ready(ulong id, int width = 64, int height = 32)
        {
            Info(id, width, height);
            State(id, Protocol.ResourceState.Ready);
        }

        public void Failed(ulong id, string error)
        {
            Error(id, error);
            State(id, Protocol.ResourceState.Failed);
        }
    }
}
