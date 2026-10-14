namespace UTM4WIN.Application;

public sealed record VmDisplayFrame(int Width, int Height, ReadOnlyMemory<byte> Pixels, long Sequence);

public interface IVmDisplaySession {
    bool CanReconnect {
        get;
    }
    long ConnectionGeneration {
        get;
    }
    bool IsConnected {
        get;
    }
    string Error {
        get;
    }
    VmDisplayFrame? ReadFrame();
    void KeyDown(uint symbol);
    void KeyUp(uint symbol);
    void Pointer(ushort x, ushort y, byte buttons);
    Task ReleaseInputAsync();
    Task DisconnectAsync();
    Task ReconnectAsync(CancellationToken cancellationToken = default);
}
