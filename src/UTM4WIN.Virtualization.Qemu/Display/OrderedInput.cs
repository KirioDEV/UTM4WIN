using System.Threading.Channels;
namespace UTM4WIN.Virtualization.Qemu.Display;

internal sealed record InputPacket(string Kind, uint Key = 0, bool Down = false, ushort X = 0, ushort Y = 0, byte Buttons = 0);
internal sealed class OrderedInput : IAsyncDisposable {
    private sealed record Batch(InputPacket[] Packets, TaskCompletionSource? Barrier = null);
    private readonly Channel<Batch> _queue = Channel.CreateBounded<Batch>(new BoundedChannelOptions(64) { SingleReader = true, SingleWriter = true });
    private readonly CancellationTokenSource _stop = new();
    private readonly HashSet<uint> _held = [];
    private readonly Func<InputPacket, CancellationToken, Task> _send;
    private readonly Task _worker;
    private ushort _lastX, _lastY;
    private byte _buttons;
    private Exception? _fault;
    public int HeldCount => _held.Count;
    public byte Buttons => _buttons;
    public Exception? Fault => _fault;
    public OrderedInput(VncConnection client) : this((packet, token) => packet.Kind == "Key" ?
        client.SendKeyStateAsync(packet.Key, packet.Down, token) : client.SendPointerStateAsync(packet.X, packet.Y, packet.Buttons, token)) {
    }
    public OrderedInput(Func<InputPacket, CancellationToken, Task> send) {
        _send = send;
        _worker = Task.Run(Run);
    }
    public void Down(uint key) {
        if (_held.Contains(key))
            return;
        if (_held.Count >= 32)
            throw new InvalidOperationException("Held-key diagnostic limit exceeded.");
        Add([new("Key", key, true)]);
        _held.Add(key);
    }
    public void Up(uint key) {
        if (!_held.Contains(key))
            return;
        Add([new("Key", key, false)]);
        _held.Remove(key);
    }
    public void Pointer(ushort x, ushort y, byte buttons) {
        Add([new("Pointer", X: x, Y: y, Buttons: buttons)]);
        _lastX = x;
        _lastY = y;
        _buttons = buttons;
    }
    public void Release(string reason) {
        var packets = _held.Order().Select(key => new InputPacket("Key", key, false)).ToList();
        if (_buttons != 0)
            packets.Add(new("Pointer", X: _lastX, Y: _lastY, Buttons: 0));
        if (packets.Count != 0)
            Add(packets.ToArray());
        _held.Clear();
        _buttons = 0;
    }
    public void Abort(string reason) {
        _held.Clear();
        _buttons = 0;
        _stop.Cancel();
        _queue.Writer.TryComplete();
    }
    public Task FlushAsync() {
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Add([], barrier);
        return barrier.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
    private void Add(InputPacket[] packets, TaskCompletionSource? barrier = null) {
        if (_fault is not null)
            throw new IOException("Input transmission failed.", _fault);
        _stop.Token.ThrowIfCancellationRequested();
        if (!_queue.Writer.TryWrite(new(packets, barrier))) {
            _fault = new IOException("Input queue full or closed; input must be disconnected.");
            _stop.Cancel();
            throw _fault;
        }
    }
    private async Task Run() {
        Batch? current = null;
        try {
            await foreach (Batch batch in _queue.Reader.ReadAllAsync(_stop.Token)) {
                current = batch;
                foreach (InputPacket packet in batch.Packets) {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(3));
                    await _send(packet, deadline.Token).WaitAsync(deadline.Token);
                }
                batch.Barrier?.TrySetResult();
                current = null;
            }
        } catch (Exception error) {
            _fault = error;
            current?.Barrier?.TrySetException(error);
            _queue.Writer.TryComplete(error);
            while (_queue.Reader.TryRead(out Batch? pending))
                pending.Barrier?.TrySetException(error);
        }
    }
    public async ValueTask DisposeAsync() {
        _queue.Writer.TryComplete();
        try {
            await _worker.WaitAsync(TimeSpan.FromSeconds(5));
        } finally { _stop.Cancel(); _stop.Dispose(); }
    }
}


