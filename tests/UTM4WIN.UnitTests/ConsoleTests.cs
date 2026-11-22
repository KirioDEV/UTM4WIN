using System.Collections.Immutable;
using UTM4WIN.App.Views;
using UTM4WIN.Virtualization.Qemu.Display;

namespace UTM4WIN.UnitTests;

[TestClass]
public sealed class ConsoleTests {
    [TestMethod]
    public void LetterboxesAndScaledViewportsMapOnlyGuestImagePixels() {
        Assert.AreEqual(((ushort)320, (ushort)240), ConsoleWindow.MapPointer(600, 400, 1200, 800, 640, 480));
        Assert.AreEqual(ConsoleWindow.MapPointer(600, 400, 1200, 800, 640, 480), ConsoleWindow.MapPointer(900, 600, 1800, 1200, 640, 480));
        Assert.IsNull(ConsoleWindow.MapPointer(1, 400, 1200, 800, 640, 480));
        Assert.IsNull(ConsoleWindow.MapPointer(double.NaN, 1, 100, 100, 640, 480));
        Assert.IsNull(ConsoleWindow.MapPointer(1, 1, 100, 100, 4096, 2160));
    }

    [TestMethod]
    public async Task InputReleaseDrainsHeldKeysAndButtonsInWireOrder() {
        var sent = new List<InputPacket>();
        await using var input = new OrderedInput((packet, _) => { sent.Add(packet); return Task.CompletedTask; });
        input.Down(0xffe1);
        input.Down(97);
        input.Down(97);
        input.Pointer(10, 20, 1);
        input.Release("Test focus loss");
        await input.FlushAsync();
        Assert.AreEqual(0, input.HeldCount);
        Assert.AreEqual(0, input.Buttons);
        Assert.AreEqual(6, sent.Count);
        Assert.IsTrue(sent.Take(2).All(packet => packet.Down));
        Assert.IsTrue(sent.Skip(3).Take(2).All(packet => !packet.Down));
        Assert.AreEqual((byte)0, sent[^1].Buttons);
    }

    [TestMethod]
    public async Task PointerFloodWhileSenderIsBusyCoalescesInsteadOfFaulting() {
        var sent = new List<InputPacket>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var input = new OrderedInput(async (packet, _) => { await gate.Task; lock (sent) sent.Add(packet); });
        for (ushort i = 0; i < 10_000; i++)
            input.Pointer(i, i, 0);
        Assert.IsNull(input.Fault);
        gate.SetResult();
        await input.FlushAsync();
        Assert.IsNull(input.Fault);
        Assert.IsTrue(sent.Count <= 2, $"Expected coalesced moves, got {sent.Count}.");
        Assert.AreEqual((ushort)9_999, sent[^1].X);
    }

    [TestMethod]
    public async Task CoalescedMovesKeepOrderAroundButtonAndKeyEvents() {
        var sent = new List<InputPacket>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var input = new OrderedInput(async (packet, _) => { await gate.Task; lock (sent) sent.Add(packet); });
        input.Pointer(1, 1, 0);
        input.Pointer(2, 2, 0);
        input.Pointer(3, 3, 0);
        input.Pointer(3, 3, 1);
        input.Pointer(4, 4, 1);
        input.Pointer(5, 5, 1);
        input.Down(97);
        input.Pointer(6, 6, 1);
        input.Up(97);
        input.Pointer(7, 7, 0);
        gate.SetResult();
        await input.FlushAsync();
        string wire = string.Join(" ", sent.Select(packet => packet.Kind == "Key" ? $"K{(packet.Down ? "d" : "u")}" : $"P{packet.X}/{packet.Buttons}"));
        // The worker may claim the first move slot before later moves arrive, so only the tail is deterministic.
        Assert.IsTrue(wire.EndsWith("P3/0 P3/1 P5/1 Kd P6/1 Ku P7/0", StringComparison.Ordinal), wire);
        Assert.IsTrue(sent.Count <= 8, wire);
    }

    [TestMethod]
    public void ServerInitBoundsRejectLargeImagesAndNamesBeforeLibraryAllocation() {
        byte[] oversized = new byte[24];
        oversized[0] = 0x10;
        using var stream = new InitReadStream(new MemoryStream(oversized));
        Assert.Throws<InvalidDataException>(() => stream.Read(new byte[24]));
    }
}
