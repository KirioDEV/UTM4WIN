namespace UTM4WIN.Application;

public sealed record VmDisplayFrame(int Width, int Height, ReadOnlyMemory<byte> Pixels, long Sequence);

public readonly record struct VmDisplayFrameInfo(int Width, int Height, long Sequence);

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
    // Returns an owned copy of the current BGRA frame; intended for diagnostics, not per-frame rendering.
    VmDisplayFrame? ReadFrame();
    // Returns the current frame size and sequence without copying pixels.
    VmDisplayFrameInfo? ReadFrameInfo();
    // Copies the current BGRA frame when destination has exactly Width * Height * 4 bytes.
    bool TryCopyFrame(Span<byte> destination, out VmDisplayFrameInfo info);
    void KeyDown(uint symbol);
    void KeyUp(uint symbol);
    void Pointer(ushort x, ushort y, byte buttons);
    Task ReleaseInputAsync();
    Task DisconnectAsync();
    Task ReconnectAsync(CancellationToken cancellationToken = default);
}
