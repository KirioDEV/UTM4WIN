using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace UTM4WIN.Virtualization.Qemu;

internal sealed class QmpClient {
    private readonly TextReader _reader;
    private readonly TextWriter _writer;
    private readonly SemaphoreSlim _writeGate = new(1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly TaskCompletionSource<JsonElement> _greeting = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _nextId;
    private Exception? _failure;
    public Task ReaderCompletion {
        get;
    }
    public event Action<string>? EventReceived;

    public QmpClient(TextReader reader, TextWriter writer) {
        _reader = reader;
        _writer = writer;
        ReaderCompletion = Task.Run(ReadAsync);
    }

    public async Task NegotiateAsync(CancellationToken cancellationToken) {
        var greeting = await _greeting.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        var version = greeting.GetProperty("version").GetProperty("qemu");
        if (version.GetProperty("major").GetInt32() != 11 || version.GetProperty("minor").GetInt32() != 1)
            throw new IOException("Unexpected QMP server version.");
        await ExecuteAsync("qmp_capabilities", cancellationToken).ConfigureAwait(false);
    }

    public async Task<JsonElement> ExecuteAsync(string command, CancellationToken cancellationToken) {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        long id = Interlocked.Increment(ref _nextId);
        var response = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _writeGate.WaitAsync(deadline.Token).ConfigureAwait(false);
        try {
            if (Volatile.Read(ref _failure) is Exception failure)
                throw new IOException("QMP is disconnected.", failure);
            if (_pending.Count >= 8)
                throw new IOException("Too many pending QMP commands.");
            _pending[id] = response;
            string json = JsonSerializer.Serialize(new {
                execute = command,
                id
            });
            await _writer.WriteLineAsync(json.AsMemory(), deadline.Token).ConfigureAwait(false);
            await _writer.FlushAsync(deadline.Token).ConfigureAwait(false);
        } catch { _pending.TryRemove(id, out _); throw; } finally { _writeGate.Release(); }
        try {
            return await response.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
        } finally { _pending.TryRemove(id, out _); }
    }

    private async Task ReadAsync() {
        var frame = new StringBuilder();
        char[] buffer = new char[2048];
        Exception failure;
        try {
            int count;
            while ((count = await _reader.ReadAsync(buffer).ConfigureAwait(false)) != 0) {
                for (int index = 0; index < count; index++) {
                    char character = buffer[index];
                    if (character == '\n') {
                        if (frame.Length != 0)
                            Dispatch(frame.ToString());
                        frame.Clear();
                    } else {
                        if (frame.Length >= 65536)
                            throw new IOException("QMP message exceeded 64 Ki characters.");
                        frame.Append(character);
                    }
                }
            }
            failure = new IOException("QMP stream closed.");
        } catch (Exception error) { failure = new IOException("QMP reader failed.", error); }
        Volatile.Write(ref _failure, failure);
        _greeting.TrySetException(failure);
        foreach (var request in _pending.Values)
            request.TrySetException(failure);
    }

    private void Dispatch(string json) {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowDuplicateProperties = false, MaxDepth = 32 });
        var root = document.RootElement;
        if (root.TryGetProperty("QMP", out var greeting)) {
            if (!_greeting.TrySetResult(greeting.Clone()))
                throw new IOException("Duplicate QMP greeting.");
        } else if (root.TryGetProperty("event", out var eventName)) {
            EventReceived?.Invoke(eventName.GetString()!);
        } else if (root.TryGetProperty("id", out var idElement)) {
            if (idElement.ValueKind != JsonValueKind.Number || !idElement.TryGetInt64(out long id) || !_pending.TryRemove(id, out var response))
                return; // Late/unknown replies are never replayed.
            if (root.TryGetProperty("error", out var error))
                response.TrySetException(new IOException("QMP: " + error.GetProperty("desc").GetString()));
            else if (root.TryGetProperty("return", out var result))
                response.TrySetResult(result.Clone());
            else
                response.TrySetException(new IOException("QMP response has no result."));
        } else
            throw new IOException("Unexpected QMP message.");
    }
}
