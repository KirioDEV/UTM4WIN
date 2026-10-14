using UTM4WIN.Application;

namespace UTM4WIN.Virtualization.Qemu.Display;

internal sealed class VncDisplaySession(DisplayMaterial material) : IVmDisplaySession {
    internal DisplayMaterial Material => material;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly object _inputGate = new();
    private VncConnection? _connection;
    private OrderedInput? _input;
    private volatile bool _ended;
    private long _generation;
    public bool CanReconnect => !_ended;
    public long ConnectionGeneration => Interlocked.Read(ref _generation);
    public bool IsConnected => _connection is { ProtocolFault: null, State: "Connected" } && _input is { Fault: null };
    public string Error => _connection?.ProtocolFault?.Message ?? _input?.Fault?.Message ?? "";
    public VmDisplayFrame? ReadFrame() => IsConnected ? _connection?.ReadFrame() : null;

    public async Task ReconnectAsync(CancellationToken cancellationToken = default) {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            if (_ended)
                throw new IOException("The VM session has ended. Start the VM before reconnecting its display.");
            await CloseCoreAsync().ConfigureAwait(false);
            var connection = new VncConnection();
            _connection = connection;
            try {
                await connection.ConnectAsync(material.Port, material.Certificates, false, false, cancellationToken).ConfigureAwait(false);
                lock (_inputGate) {
                    _input = new(connection);
                    Interlocked.Increment(ref _generation);
                }
            } catch { await connection.CloseAsync().ConfigureAwait(false); _connection = null; throw; }
        } finally { _gate.Release(); }
    }

    public void KeyDown(uint symbol) {
        lock (_inputGate) {
            if (IsConnected)
                _input!.Down(symbol);
        }
    }
    public void KeyUp(uint symbol) {
        lock (_inputGate) {
            if (IsConnected)
                _input!.Up(symbol);
        }
    }
    public void Pointer(ushort x, ushort y, byte buttons) {
        lock (_inputGate) {
            if (IsConnected)
                _input!.Pointer(x, y, buttons);
        }
    }
    public Task ReleaseInputAsync() {
        lock (_inputGate) {
            if (_input is null)
                return Task.CompletedTask;
            if (!IsConnected) {
                _input.Abort("Disconnected");
                return Task.CompletedTask;
            }
            _input.Release("Focus or session boundary");
            return _input.FlushAsync();
        }
    }

    public async Task CloseAsync() {
        await _gate.WaitAsync().ConfigureAwait(false);
        try {
            await CloseCoreAsync().ConfigureAwait(false);
        } finally { _gate.Release(); }
    }
    public Task DisconnectAsync() => CloseAsync();
    public Task EndAsync() {
        _ended = true;
        return CloseAsync();
    }

    private async Task CloseCoreAsync() {
        var connection = _connection;
        OrderedInput? input;
        lock (_inputGate) {
            input = _input;
            _input = null;
        }
        if (input is not null) {
            try {
                if (connection?.ProtocolFault is null) {
                    input.Release("Disconnect");
                    await input.FlushAsync().ConfigureAwait(false);
                } else
                    input.Abort("Connection lost");
            } catch { input.Abort("Release failed"); }
            await input.DisposeAsync().ConfigureAwait(false);
        }
        if (connection is not null)
            await connection.CloseAsync().ConfigureAwait(false);
        _connection = null;
    }
}
