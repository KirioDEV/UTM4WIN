using System.Collections.Immutable;
using System.Runtime.InteropServices;
using MarcusW.VncClient;
using UTM4WIN.App.Views;
using UTM4WIN.Application;
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

    [TestMethod]
    public void CompletedUpdatesPublishOnlyTouchedRectanglesWithOpaqueAlpha() {
        using var connection = new VncConnection();
        Assert.IsNull(connection.ReadFrameInfo());
        Assert.IsFalse(connection.TryCopyFrame(new byte[32], out _));
        DecodeUpdate(connection, 4, 2, Enumerable.Repeat((byte)1, 32).ToArray(), new Rectangle(new Position(0, 0), new Size(4, 2)));
        var copy = new byte[32];
        Assert.IsTrue(connection.TryCopyFrame(copy, out var first));
        Assert.AreEqual(new VmDisplayFrameInfo(4, 2, 1), first);
        Assert.IsTrue(copy.Where((_, index) => index % 4 == 3).All(alpha => alpha == 255));
        Assert.IsTrue(copy.Where((_, index) => index % 4 != 3).All(value => value == 1));

        // The decoder buffer changes everywhere, but the update only reports pixel (1, 1) as touched.
        DecodeUpdate(connection, 4, 2, Enumerable.Repeat((byte)9, 32).ToArray(), new Rectangle(new Position(1, 1), new Size(1, 1)));
        Assert.IsTrue(connection.TryCopyFrame(copy, out var second));
        Assert.AreEqual(2, second.Sequence);
        int touched = (1 * 4 + 1) * 4;
        for (int index = 0; index < copy.Length; index++) {
            byte expected = index % 4 == 3 ? (byte)255 : index >= touched && index < touched + 4 ? (byte)9 : (byte)1;
            Assert.AreEqual(expected, copy[index], $"Byte {index}");
        }
        Assert.IsFalse(connection.TryCopyFrame(new byte[16], out _), "A destination of the wrong size must not receive a partial frame.");
    }

    [TestMethod]
    public void RejectedUpdateIsNotPublishedAndForcesTheNextUpdateToPublishTheWholeImage() {
        using var connection = new VncConnection();
        DecodeUpdate(connection, 2, 1, Enumerable.Repeat((byte)1, 8).ToArray(), new Rectangle(new Position(0, 0), new Size(2, 1)));
        connection.BeginUpdate();
        using (var lease = connection.GrabFramebufferReference(new Size(2, 1), ImmutableHashSet<Screen>.Empty))
            Marshal.Copy(Enumerable.Repeat((byte)7, 8).ToArray(), 0, lease.Address, 8);
        connection.RejectUpdate();
        var copy = new byte[8];
        Assert.IsTrue(connection.TryCopyFrame(copy, out var info));
        Assert.AreEqual(1, info.Sequence);
        Assert.AreEqual((byte)1, copy[0]);
        Assert.AreEqual(1, connection.RejectedUpdates);

        DecodeUpdate(connection, 2, 1, null, new Rectangle(new Position(0, 0), new Size(1, 1)));
        Assert.IsTrue(connection.TryCopyFrame(copy, out info));
        Assert.AreEqual(2, info.Sequence);
        Assert.AreEqual((byte)7, copy[4], "Pixels left by the rejected update must not stay hidden behind a partial copy.");
    }

    [TestMethod]
    public void DesktopResizePublishesTheNewSizeCompletely() {
        using var connection = new VncConnection();
        DecodeUpdate(connection, 2, 2, Enumerable.Repeat((byte)1, 16).ToArray(), new Rectangle(new Position(0, 0), new Size(2, 2)));
        DecodeUpdate(connection, 3, 1, Enumerable.Repeat((byte)5, 12).ToArray(), new Rectangle(new Position(0, 0), new Size(1, 1)));
        Assert.AreEqual(new VmDisplayFrameInfo(3, 1, 2), connection.ReadFrameInfo());
        Assert.IsFalse(connection.TryCopyFrame(new byte[16], out _));
        var copy = new byte[12];
        Assert.IsTrue(connection.TryCopyFrame(copy, out _));
        Assert.IsTrue(copy.Where((_, index) => index % 4 != 3).All(value => value == 5));
    }

    private static void DecodeUpdate(VncConnection connection, int width, int height, byte[]? pixels, params Rectangle[] touched) {
        connection.BeginUpdate();
        using (var lease = connection.GrabFramebufferReference(new Size(width, height), ImmutableHashSet<Screen>.Empty)) {
            if (pixels is not null)
                Marshal.Copy(pixels, 0, lease.Address, pixels.Length);
            foreach (var rectangle in touched)
                connection.MarkDirty(rectangle);
        }
        connection.CompleteUpdate();
    }
}
