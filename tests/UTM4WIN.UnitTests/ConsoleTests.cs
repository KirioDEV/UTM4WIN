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
    public void ServerInitBoundsRejectLargeImagesAndNamesBeforeLibraryAllocation() {
        byte[] oversized = new byte[24];
        oversized[0] = 0x10;
        using var stream = new InitReadStream(new MemoryStream(oversized));
        Assert.Throws<InvalidDataException>(() => stream.Read(new byte[24]));
    }
}
