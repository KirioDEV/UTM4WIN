using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using UTM4WIN.Virtualization.Qemu;

namespace UTM4WIN.UnitTests;

[TestClass]
public sealed class QmpTests {
    [TestMethod]
    public async Task FragmentedGreetingEventsAndUnknownIdsDoNotCorruptResponses() {
        var reader = new FeedReader();
        var writer = new FeedWriter();
        var client = new QmpClient(reader, writer);
        int events = 0;
        client.EventReceived += _ => events++;
        writer.OnCommand = json => {
            using var command = JsonDocument.Parse(json);
            long id = command.RootElement.GetProperty("id").GetInt64();
            reader.Add("{\"event\":\"STOP\"}\r\n{\"id\":9876,\"return\":{}}\r\n{\"id\":\"unknown\",\"return\":{}}\r\n");
            reader.Add($"{{\"id\":{id},\"return\":{{\"status\":\"running\"}}}}\r\n");
        };
        reader.Add("{\"Q");
        reader.Add("MP\":{\"version\":{\"qemu\":{\"major\":11,\"minor\":1}},\"capabilities\":[]}}\r\n");
        await client.NegotiateAsync(CancellationToken.None);
        Assert.AreEqual("running", (await client.ExecuteAsync("query-status", CancellationToken.None)).GetProperty("status").GetString());
        Assert.AreEqual(2, events);
        reader.End();
        await client.ReaderCompletion;
    }

    [TestMethod]
    public async Task CanceledRequestDropsLateResponseWithoutReusingItsId() {
        var reader = new FeedReader();
        var writer = new FeedWriter();
        var client = new QmpClient(reader, writer);
        long canceledId = 0;
        writer.OnCommand = json => {
            using var command = JsonDocument.Parse(json);
            long id = command.RootElement.GetProperty("id").GetInt64();
            if (command.RootElement.GetProperty("execute").GetString() == "stop") {
                canceledId = id;
                return;
            }
            if (canceledId != 0)
                reader.Add($"{{\"id\":{canceledId},\"return\":{{\"wrong\":true}}}}\r\n");
            reader.Add($"{{\"id\":{id},\"return\":{{}}}}\r\n");
        };
        reader.Greet();
        await client.NegotiateAsync(CancellationToken.None);
        using var canceled = new CancellationTokenSource(30);
        await Assert.ThrowsAsync<OperationCanceledException>(() => client.ExecuteAsync("stop", canceled.Token));
        var result = await client.ExecuteAsync("query-status", CancellationToken.None);
        Assert.IsFalse(result.TryGetProperty("wrong", out _));
        reader.End();
        await client.ReaderCompletion;
    }

    [TestMethod]
    public async Task ProtocolErrorAndOversizedFrameFailPendingCommands() {
        var reader = new FeedReader();
        var writer = new FeedWriter();
        var client = new QmpClient(reader, writer);
        writer.OnCommand = json => {
            using var command = JsonDocument.Parse(json);
            long id = command.RootElement.GetProperty("id").GetInt64();
            reader.Add($"{{\"id\":{id},\"error\":{{\"class\":\"GenericError\",\"desc\":\"denied\"}}}}\r\n");
        };
        reader.Greet();
        await Assert.ThrowsAsync<IOException>(() => client.NegotiateAsync(CancellationToken.None));
        writer.OnCommand = _ => { for (int i = 0; i < 34; i++) reader.Add(new string('x', 2048)); };
        await Assert.ThrowsAsync<IOException>(() => client.ExecuteAsync("query-status", CancellationToken.None));
        await client.ReaderCompletion;
        reader.End();
    }

    private sealed class FeedReader : TextReader {
        private readonly Channel<string> _chunks = Channel.CreateUnbounded<string>();
        public void Add(string chunk) => _chunks.Writer.TryWrite(chunk);
        public void End() => _chunks.Writer.TryComplete();
        public void Greet() => Add("{\"QMP\":{\"version\":{\"qemu\":{\"major\":11,\"minor\":1}},\"capabilities\":[]}}\r\n");
        public override async Task<int> ReadAsync(char[] buffer, int index, int count) {
            if (!await _chunks.Reader.WaitToReadAsync())
                return 0;
            string chunk = await _chunks.Reader.ReadAsync();
            Assert.IsTrue(chunk.Length <= count);
            chunk.CopyTo(0, buffer, index, chunk.Length);
            return chunk.Length;
        }
    }
    private sealed class FeedWriter : TextWriter {
        public Action<string> OnCommand = _ => { };
        public override Encoding Encoding => Encoding.UTF8;
        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default) {
            OnCommand(buffer.ToString());
            return Task.CompletedTask;
        }
    }
}
