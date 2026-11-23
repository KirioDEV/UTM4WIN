using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MarcusW.VncClient;
using MarcusW.VncClient.Protocol;
using MarcusW.VncClient.Protocol.Implementation;
using MarcusW.VncClient.Protocol.Implementation.EncodingTypes.Frame;
using MarcusW.VncClient.Protocol.Implementation.EncodingTypes.Pseudo;
using MarcusW.VncClient.Protocol.Implementation.MessageTypes.Outgoing;
using MarcusW.VncClient.Protocol.SecurityTypes;
using MarcusW.VncClient.Protocol.Services;
using MarcusW.VncClient.Rendering;
using MarcusW.VncClient.Security;
using MarcusW.VncClient.Protocol.Implementation.Services.Transports;
using Microsoft.Extensions.Logging.Abstractions;

namespace UTM4WIN.Virtualization.Qemu.Display;

internal sealed class VncConnection : IRenderTarget, IDisposable {
    private RfbConnection? _connection;
    private readonly object _gate = new();
    // The decoder writes into _framebuffer outside _gate. Completed updates are copied into the reused
    // _published buffer under _gate, limited to the rectangles the update touched, so a frame costs neither a
    // full-buffer allocation nor a full-buffer copy.
    private byte[] _framebuffer = [];
    private Size _framebufferSize;
    private byte[] _published = [];
    private int _publishedWidth;
    private int _publishedHeight;
    private bool _staged;
    private bool _publishAll = true;
    private readonly List<Rectangle> _dirty = [];
    private PinnedVeNCrypt? _security;
    private readonly bool _guarded;
    private readonly string _codec;
    public VncConnection() {
        _codec = "ZLib";
        _guarded = true;
    }
    public bool Guarded => _guarded;
    public Exception? ProtocolFault => _connection?.InterruptionCause;
    public void DropTransportForTest() => _security?.Dispose();
    public string State => _connection?.ConnectionState.ToString() ?? "NotConnected";
    public Dictionary<string, int> WireRectangles { get; } = [];
    public long CompressedInputBytes {
        get; internal set;
    }
    public long EquivalentRawBytes {
        get; internal set;
    }
    public int RejectedUpdates {
        get; private set;
    }
    public int ReceivedFrames {
        get; private set;
    }
    public string TlsProtocol => _security?.TlsProtocol ?? "";
    public bool ServerCertificateRejected => _security?.ServerCertificateRejected ?? false;
    public async Task ConnectAsync(int port, DisplayCertificates certificates, bool wrongPin, bool omitClient, CancellationToken token) {
        var implementation = new RestrictedProtocol(this, context => {
            _security = new PinnedVeNCrypt(context, certificates, wrongPin, omitClient, _guarded);
            return new ISecurityType[] { _security };
        });
        var client = new VncClient(NullLoggerFactory.Instance, implementation);
        try {
            var parameters = new ConnectParameters {
                TransportParameters = new TcpTransportParameters { Host = "127.0.0.1", Port = port },
                ConnectTimeout = TimeSpan.FromSeconds(10),
                MaxReconnectAttempts = 0,
                ReuseAuthenticationInputOnReconnect = false,
                AuthenticationHandler = new RejectPrompt(),
                AllowSharedConnection = true,
                InitialRenderTarget = this,
                // Minimum gap between incremental update requests; caps the guest image at about 60 updates per second.
                FramebufferUpdateDelay = TimeSpan.FromMilliseconds(16)
            };
            _connection = await Task.Run(() => client.ConnectAsync(parameters, token), token).ConfigureAwait(false);
        } catch (Exception error) {
            _security?.Dispose();
            if (token.IsCancellationRequested)
                throw new OperationCanceledException("Core connection canceled.", error, token);
            if (ServerCertificateRejected)
                throw new AuthenticationException("Pinned server validation rejected.", error);
            throw;
        }
    }
    public Task KeyAsync(uint key, CancellationToken token) {
        token.ThrowIfCancellationRequested();
        _connection!.EnqueueMessage(new KeyEventMessage(true, (KeySymbol)key), token);
        _connection.EnqueueMessage(new KeyEventMessage(false, (KeySymbol)key), token);
        return Task.CompletedTask;
    }
    public Task SendKeyStateAsync(uint key, bool down, CancellationToken token) =>
        (_connection ?? throw new InvalidOperationException("Not connected.")).SendMessageAsync(new KeyEventMessage(down, (KeySymbol)key), token);
    public Task SendPointerStateAsync(ushort x, ushort y, byte buttons, CancellationToken token) =>
        (_connection ?? throw new InvalidOperationException("Not connected.")).SendMessageAsync(new PointerEventMessage(new Position(x, y), (MouseButtons)buttons), token);
    public Task PointerAsync(ushort x, ushort y, CancellationToken token) {
        token.ThrowIfCancellationRequested();
        var position = new Position(x, y);
        _connection!.EnqueueMessage(new PointerEventMessage(position, MouseButtons.None), token);
        _connection.EnqueueMessage(new PointerEventMessage(position, MouseButtons.Left), token);
        _connection.EnqueueMessage(new PointerEventMessage(position, MouseButtons.None), token);
        return Task.CompletedTask;
    }
    public IFramebufferReference GrabFramebufferReference(Size size, IImmutableSet<Screen> layout) {
        if (size.Width < 1 || size.Height < 1 || size.Width > 2048 || size.Height > 2048)
            throw new InvalidDataException("Framebuffer exceeds diagnostic bounds.");
        int length = checked(size.Width * size.Height * 4);
        if (_framebuffer.Length != length || _framebufferSize != size) {
            _framebuffer = new byte[length];
            _framebufferSize = size;
            _publishAll = true;
        }
        return new Lease(_framebuffer, size, () => {
            lock (_gate) {
                if (_guarded)
                    _staged = true;
                else {
                    _publishAll = true;
                    PublishLocked();
                }
            }
        });
    }
    // Called by the guarded decoder for every validated top-level rectangle of the current update.
    internal void MarkDirty(in Rectangle rectangle) => _dirty.Add(rectangle);
    public async Task CloseAsync() {
        if (_connection is { } connection) {
            _connection = null;
            // Interrupt an idle TLS read before waiting for protocol workers.
            _security?.Dispose();
            try {
                await Task.Run(() => connection.CloseAsync()).WaitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
            } catch { _security?.Dispose(); throw; } finally { await Task.Run(() => connection.Dispose()).WaitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false); }
        }
    }
    public void Dispose() => _security?.Dispose();
    internal void BeginUpdate() {
        lock (_gate) {
            _staged = false;
            _dirty.Clear();
        }
    }
    internal void CompleteUpdate() {
        lock (_gate) {
            if (_staged)
                PublishLocked();
            _staged = false;
            _dirty.Clear();
        }
    }
    internal void RejectUpdate() {
        lock (_gate) {
            _staged = false;
            _dirty.Clear();
            // The working buffer may hold part of the rejected update; never publish it partially later.
            _publishAll = true;
            RejectedUpdates++;
        }
    }
    internal UTM4WIN.Application.VmDisplayFrameInfo? ReadFrameInfo() {
        lock (_gate) {
            return ReceivedFrames > 0 ? new(_publishedWidth, _publishedHeight, ReceivedFrames) : null;
        }
    }
    internal bool TryCopyFrame(Span<byte> destination, out UTM4WIN.Application.VmDisplayFrameInfo info) {
        lock (_gate) {
            info = new(_publishedWidth, _publishedHeight, ReceivedFrames);
            if (ReceivedFrames == 0 || destination.Length != _published.Length)
                return false;
            _published.CopyTo(destination);
            return true;
        }
    }
    internal UTM4WIN.Application.VmDisplayFrame? ReadFrame() {
        lock (_gate) {
            return ReceivedFrames > 0 ? new(_publishedWidth, _publishedHeight, (byte[])_published.Clone(), ReceivedFrames) : null;
        }
    }
    private void PublishLocked() {
        int width = _framebufferSize.Width, height = _framebufferSize.Height;
        if (_publishAll || _publishedWidth != width || _publishedHeight != height || _published.Length != _framebuffer.Length) {
            if (_published.Length != _framebuffer.Length)
                _published = new byte[_framebuffer.Length];
            _publishedWidth = width;
            _publishedHeight = height;
            CopyOpaque(0, 0, width, height);
            _publishAll = false;
        } else {
            foreach (var rectangle in _dirty)
                CopyOpaque(rectangle.Position.X, rectangle.Position.Y, rectangle.Size.Width, rectangle.Size.Height);
        }
        ReceivedFrames++;
    }
    // Copies one region row by row and forces the alpha byte of each BGRA pixel to opaque.
    private void CopyOpaque(int x, int y, int width, int height) {
        int stride = _publishedWidth * 4, rowLength = width * 4;
        for (int row = y; row < y + height; row++) {
            int offset = row * stride + x * 4;
            var target = _published.AsSpan(offset, rowLength);
            _framebuffer.AsSpan(offset, rowLength).CopyTo(target);
            for (int i = 3; i < rowLength; i += 4)
                target[i] = 255;
        }
    }
    private sealed class Lease : IFramebufferReference {
        private readonly GCHandle _pin;
        private readonly Action _completed;
        private bool _disposed;
        public Lease(byte[] pixels, Size size, Action completed) {
            _pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            Size = size;
            _completed = completed;
        }
        public IntPtr Address => _pin.AddrOfPinnedObject();
        public Size Size {
            get;
        }
        public PixelFormat Format => new("WinUI BGRA", 32, 24, false, true, true, 255, 255, 255, 255, 16, 8, 0, 24);
        public double HorizontalDpi => 96;
        public double VerticalDpi => 96;
        public void Dispose() {
            if (_disposed)
                return;
            _disposed = true;
            try {
                _completed();
            } finally { _pin.Free(); }
        }
    }
    private sealed class RejectPrompt : IAuthenticationHandler {
        public Task<TInput> ProvideAuthenticationInputAsync<TInput>(RfbConnection connection, ISecurityType securityType,
            IAuthenticationInputRequest<TInput> request) where TInput : class, IAuthenticationInput
            => throw new AuthenticationException("Unexpected authentication prompt.");
    }
    private sealed class RestrictedProtocol : DefaultImplementation {
        private readonly VncConnection _owner;
        public RestrictedProtocol(VncConnection owner, SecurityTypesCollectionBuilderDelegate security) : base(security,
            context => GuardProfile.Messages(context, owner), context => GuardProfile.Encodings(context, owner, owner._codec, owner._guarded)) {
            _owner = owner;
        }
        public override IZLibInflater CreateZLibInflater(RfbConnectionContext context) => _owner._guarded ? new BoundedInflater(_owner) : base.CreateZLibInflater(context);
        public override IRfbInitializer CreateRfbInitializer(RfbConnectionContext context) =>
            _owner._guarded ? new GuardInitializer(_owner._security!, base.CreateRfbInitializer(context)) : base.CreateRfbInitializer(context);
        public override IJpegDecoder CreateJpegDecoder(RfbConnectionContext context) => new DisabledJpeg();
    }
    private sealed class DisabledJpeg : IJpegDecoder {
        public void DecodeJpegTo32Bit(Span<byte> jpegBuffer, Span<byte> pixelsBuffer, int width, int height,
            PixelFormat preferred, out PixelFormat used, CancellationToken token = default)
            => throw new NotSupportedException("JPEG is disabled in this qualification profile.");
        public void Dispose() {
        }
    }
}

internal sealed class PinnedVeNCrypt : ISecurityType, IDisposable {
    private readonly RfbConnectionContext _context;
    private readonly DisplayCertificates _certificates;
    private readonly bool _wrongPin;
    private readonly bool _omitClient;
    private readonly bool _guarded;
    private AuthenticatedTransport? _transport;
    public PinnedVeNCrypt(RfbConnectionContext context, DisplayCertificates certificates, bool wrongPin, bool omitClient, bool guarded) {
        _context = context;
        _certificates = certificates;
        _wrongPin = wrongPin;
        _omitClient = omitClient;
        _guarded = guarded;
    }
    public byte Id => 19;
    public string Name => "UTM4WIN pinned mutually authenticated VeNCrypt";
    public int Priority => 100;
    public bool ServerCertificateRejected {
        get; private set;
    }
    public string TlsProtocol { get; private set; } = "";
    public async Task<AuthenticationResult> AuthenticateAsync(IAuthenticationHandler handler, CancellationToken token = default) {
        Stream stream = _context.Transport!.Stream;
        async Task<byte[]> Read(int length) {
            var bytes = new byte[length];
            await stream.ReadExactlyAsync(bytes, token);
            return bytes;
        }
        async Task Write(byte[] bytes) => await stream.WriteAsync(bytes, token);
        if (!(await Read(2)).SequenceEqual(new byte[] { 0, 2 }))
            throw new AuthenticationException("VeNCrypt 0.2 required.");
        await Write([0, 2]);
        if ((await Read(1))[0] != 0)
            throw new AuthenticationException("VeNCrypt version rejected.");
        int count = (await Read(1))[0];
        byte[] subtypes = await Read(count * 4);
        bool supported = Enumerable.Range(0, count).Any(i => BinaryPrimitives.ReadUInt32BigEndian(subtypes.AsSpan(i * 4)) == 260);
        if (!supported)
            throw new AuthenticationException("X509None required; no fallback.");
        byte[] selected = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(selected, 260);
        await Write(selected);
        if ((await Read(1))[0] != 1)
            throw new AuthenticationException("TLS subtype rejected.");
        byte[] pin = _wrongPin ? RandomNumberGenerator.GetBytes(32) : _certificates.Server.GetCertHash(HashAlgorithmName.SHA256);
        var tls = new SslStream(stream, true, (_, certificate, _, errors) => {
            bool accepted = certificate is not null && errors == SslPolicyErrors.None &&
                CryptographicOperations.FixedTimeEquals(certificate.GetCertHash(HashAlgorithmName.SHA256), pin);
            ServerCertificateRejected = !accepted;
            return accepted;
        });
        try {
            var policy = new X509ChainPolicy {
                TrustMode = X509ChainTrustMode.CustomRootTrust,
                RevocationMode = X509RevocationMode.NoCheck,
                DisableCertificateDownloads = true
            };
            policy.CustomTrustStore.Add(_certificates.Authority);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions {
                AllowTlsResume = false,
                LocalCertificateSelectionCallback = (_, _, _, _, _) => _omitClient ? null : _certificates.Client,
                TargetHost = "localhost",
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateChainPolicy = policy,
                ClientCertificates = _omitClient ? new X509CertificateCollection() : new X509CertificateCollection { _certificates.Client }
            }, token);
            if (!tls.IsMutuallyAuthenticated)
                throw new AuthenticationException("Mutual TLS authentication is required; no display fallback is allowed.");
            TlsProtocol = tls.SslProtocol.ToString();
            _transport = new AuthenticatedTransport(tls, _context.Transport!, _guarded);
            return new AuthenticationResult(_transport, true);
        } catch { tls.Dispose(); _context.Transport?.Dispose(); throw; }
    }
    public Task ReadServerInitExtensionAsync(CancellationToken token = default) => Task.CompletedTask;
    public void BeginInitializationGuard() => _transport!.BeginInitializationGuard();
    public void EndInitializationGuard() => _transport!.EndInitializationGuard();
    public void Dispose() {
        if (_transport is not null)
            _transport.Dispose();
        else
            _context.Transport?.Dispose();
    }
    private sealed class AuthenticatedTransport : ITransport {
        private readonly SslStream _stream;
        private readonly ITransport _baseTransport;
        private readonly Stream _baseView;
        private Stream _view;
        private int _disposed;
        public AuthenticatedTransport(SslStream stream, ITransport baseTransport, bool guarded) {
            _stream = stream;
            _baseTransport = baseTransport;
            _baseView = guarded ? new EofReadStream(stream) : stream;
            _view = _baseView;
        }
        public void BeginInitializationGuard() => _view = new InitReadStream(_baseView);
        public void EndInitializationGuard() => _view = _baseView;
        public Stream Stream => _view;
        public bool IsEncrypted => _stream.IsAuthenticated && _stream.IsEncrypted;
        public void Dispose() {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            try {
                _baseTransport.Dispose();
            } finally { _stream.Dispose(); }
        }
    }
}


