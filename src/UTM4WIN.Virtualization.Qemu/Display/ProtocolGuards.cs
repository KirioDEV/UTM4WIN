using System.Buffers.Binary;
using MarcusW.VncClient;
using MarcusW.VncClient.Protocol;
using MarcusW.VncClient.Protocol.EncodingTypes;
using MarcusW.VncClient.Protocol.Implementation;
using MarcusW.VncClient.Protocol.Implementation.EncodingTypes.Frame;
using MarcusW.VncClient.Protocol.Implementation.EncodingTypes.Pseudo;
using MarcusW.VncClient.Protocol.Implementation.MessageTypes.Incoming;
using MarcusW.VncClient.Protocol.Implementation.Services.Communication;
using MarcusW.VncClient.Protocol.MessageTypes;
using MarcusW.VncClient.Protocol.Services;
using MarcusW.VncClient.Rendering;

namespace UTM4WIN.Virtualization.Qemu.Display;

internal static class GuardProfile {
    public static IEnumerable<IMessageType> Messages(RfbConnectionContext context, VncConnection owner) {
        foreach (var message in DefaultImplementation.GetDefaultMessageTypes(context)) {
            if (message is IIncomingMessageType incoming) {
                if (incoming.Id == 0)
                    yield return owner.Guarded ? new GuardUpdate(incoming, owner) : incoming;
                else if (incoming.Id == 2)
                    yield return incoming;
            } else
                yield return message;
        }
    }
    public static IEnumerable<IEncodingType> Encodings(RfbConnectionContext context, VncConnection owner, string codec, bool guarded) {
        yield return new GuardEncoding(new RawEncodingType(), context, owner, guarded);
        if (codec == "ZLib")
            yield return new GuardEncoding(new ZLibEncodingType(context), context, owner, guarded);

        else if (codec != "Raw")
            throw new ArgumentException("Unknown qualification codec.");
        yield return new DesktopSizeEncodingType(context);
    }
}

internal sealed class GuardEncoding(IFrameEncodingType inner, RfbConnectionContext context, VncConnection owner, bool guarded) : IFrameEncodingType {
    public int Id => inner.Id;
    public string Name => inner.Name;
    public int Priority => inner.Priority;
    public bool GetsConfirmed => inner.GetsConfirmed;
    public Color VisualizationColor => inner.VisualizationColor;
    public void ReadFrameEncoding(Stream stream, IFramebufferReference? target, in Rectangle rectangle, in Size size, in PixelFormat format) {
        bool nested = stream is InflatedReadStream;
        if (!nested) {
            lock (owner.WireRectangles)
                owner.WireRectangles[Name] = owner.WireRectangles.GetValueOrDefault(Name) + 1;
            owner.EquivalentRawBytes += checked((long)rectangle.Size.Width * rectangle.Size.Height * format.BytesPerPixel);
        }
        if (guarded && (rectangle.IsEmpty() || !rectangle.FitsInside(size)))
            throw new InvalidDataException("Frame rectangle is outside bounds.");
        if (guarded && inner.Id == 6) {
            var inflater = (BoundedInflater)context.ZLibInflater!;
            inner.ReadFrameEncoding(stream, target, rectangle, size, format);
            inflater.EnsureNoExcess();
        } else
            inner.ReadFrameEncoding(guarded && inner.Id == 0 ? new EofReadStream(stream) : stream, target, rectangle, size, format);
    }
}
internal sealed class GuardUpdate(IIncomingMessageType inner, VncConnection owner) : IIncomingMessageType {
    public byte Id => inner.Id;
    public string Name => inner.Name;
    public bool IsStandardMessageType => inner.IsStandardMessageType;
    public void ReadMessage(ITransport transport, CancellationToken token = default) {
        owner.BeginUpdate();
        try {
            byte[] header = new byte[3];
            transport.Stream.ReadExactly(header);
            if (BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(1)) > 1024)
                throw new InvalidDataException("Rectangle count exceeds qualification bound.");
            inner.ReadMessage(new BorrowedTransport(transport, new PrefixReadStream(transport.Stream, header)), token);
            owner.CompleteUpdate();
        } catch { owner.RejectUpdate(); throw; }
    }
}
internal sealed class BoundedInflater(VncConnection owner) : IZLibInflater {
    private readonly BuiltinZLibInflater _inner = new();
    private Stream? _last;
    public Stream ReadAndInflate(Stream source, int length, int id = -1, CancellationToken token = default) {
        if (length <= 0 || length > 16 * 1024 * 1024 + 65536)
            throw new InvalidDataException("Compressed length exceeds qualification bound.");
        owner.CompressedInputBytes += length;
        _last = _inner.ReadAndInflate(source, length, id, token);
        return new InflatedReadStream(_last);
    }
    public void EnsureNoExcess() {
        if (_last is not null && _last.ReadByte() != -1)
            throw new InvalidDataException("Inflated data exceeds rectangle pixel count.");
    }
    public void ResetZlibStream(int id) => _inner.ResetZlibStream(id);
    public void Dispose() => _inner.Dispose();
}
internal sealed class GuardInitializer(PinnedVeNCrypt security, IRfbInitializer inner) : IRfbInitializer {
    public async Task InitializeAsync(CancellationToken token = default) {
        security.BeginInitializationGuard();
        try {
            await inner.InitializeAsync(token).ConfigureAwait(false);
        } finally { security.EndInitializationGuard(); }
    }
}
internal sealed class InitReadStream(Stream source) : ReadProxy(source) {
    private PrefixReadStream? _prefix;
    private static void Validate(byte[] h) {
        int width = BinaryPrimitives.ReadUInt16BigEndian(h), height = BinaryPrimitives.ReadUInt16BigEndian(h.AsSpan(2));
        if (width is < 1 or > 2048 || height is < 1 or > 2048 || BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(20)) > 1024)
            throw new InvalidDataException("ServerInit dimensions/name exceed qualification bounds.");
        if (h[4] != 32 || h[5] != 24 || h[6] != 0 || h[7] != 1 ||
            BinaryPrimitives.ReadUInt16BigEndian(h.AsSpan(8)) != 255 || BinaryPrimitives.ReadUInt16BigEndian(h.AsSpan(10)) != 255 ||
            BinaryPrimitives.ReadUInt16BigEndian(h.AsSpan(12)) != 255 || h[14] != 16 || h[15] != 8 || h[16] != 0)
            throw new InvalidDataException("Unsupported ServerInit pixel format for qualification profile.");
    }
    public override int Read(Span<byte> buffer) {
        if (_prefix is null) {
            byte[] h = new byte[24];
            Source.ReadExactly(h);
            Validate(h);
            _prefix = new PrefixReadStream(Source, h);
        }
        return _prefix.Read(buffer);
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) {
        if (_prefix is null) {
            byte[] h = new byte[24];
            await Source.ReadExactlyAsync(h, token);
            Validate(h);
            _prefix = new PrefixReadStream(Source, h);
        }
        return await _prefix.ReadAsync(buffer, token);
    }
}
internal sealed class BorrowedTransport(ITransport original, Stream view) : ITransport {
    public Stream Stream => view; public bool IsEncrypted => original.IsEncrypted; public void Dispose() {
    }
}
internal class ReadProxy(Stream source) : Stream {
    protected Stream Source { get; } = source;
    public override bool CanRead => Source.CanRead;
    public override bool CanWrite => Source.CanWrite;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position {
        get => throw new NotSupportedException(); set => throw new NotSupportedException();
    }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer) => Source.Read(buffer);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => Source.ReadAsync(buffer, token);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
    public override int ReadByte() {
        Span<byte> one = stackalloc byte[1];
        return Read(one) == 0 ? -1 : one[0];
    }
    public override void Write(byte[] buffer, int offset, int count) => Source.Write(buffer, offset, count);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) => Source.WriteAsync(buffer, token);
    public override void Flush() => Source.Flush();
    public override Task FlushAsync(CancellationToken token) => Source.FlushAsync(token);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
internal class EofReadStream(Stream source) : ReadProxy(source) {
    public override int Read(Span<byte> buffer) {
        int read = Source.Read(buffer);
        if (read == 0 && buffer.Length > 0)
            throw new EndOfStreamException("Incomplete protocol payload.");
        return read;
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) {
        int read = await Source.ReadAsync(buffer, token).ConfigureAwait(false);
        if (read == 0 && buffer.Length > 0)
            throw new EndOfStreamException("Incomplete protocol payload.");
        return read;
    }
}
internal sealed class InflatedReadStream(Stream source) : EofReadStream(source);
internal sealed class PrefixReadStream(Stream source, byte[] prefix) : ReadProxy(source) {
    private int _position;
    public override int Read(Span<byte> buffer) {
        int take = Math.Min(buffer.Length, prefix.Length - _position);
        if (take == 0)
            return Source.Read(buffer);
        prefix.AsSpan(_position, take).CopyTo(buffer);
        _position += take;
        return take;
    }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) {
        token.ThrowIfCancellationRequested();
        return _position < prefix.Length ? ValueTask.FromResult(Read(buffer.Span)) : Source.ReadAsync(buffer, token);
    }
}

