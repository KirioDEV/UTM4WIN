using Microsoft.VisualStudio.TestTools.UnitTesting;
using UTM4WIN.Application;
using UTM4WIN.Domain;
using UTM4WIN.Infrastructure;
using UTM4WIN.Virtualization.Qemu;

namespace UTM4WIN.UnitTests;

[TestClass]
[TestCategory("QemuIntegration")]
public sealed class QemuCreationIntegrationTests
{
    private string _tool = null!;
    private string _root = null!;

    [TestInitialize]
    public void Initialize()
    {
        _tool = Environment.GetEnvironmentVariable("UTM4WIN_TEST_QEMU_IMG") ?? "";
        if (!File.Exists(_tool)) Assert.Inconclusive("Set UTM4WIN_TEST_QEMU_IMG to the explicitly approved qualified binary.");
        _root = Path.Combine(Path.GetTempPath(), "UTM4WIN-QEMU café, " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (_root is not null && Directory.Exists(_root) && Path.GetDirectoryName(_root) == Path.TrimEndingDirectorySeparator(Path.GetTempPath()) &&
            Path.GetFileName(_root).StartsWith("UTM4WIN-QEMU café, ", StringComparison.Ordinal)) Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public async Task RealSparseQcow2CreationRegistersOnceAndReloadsWithoutChangingIso()
    {
        string iso = Path.Combine(_root, "fixture.iso");
        byte[] bytes = new byte[17 * 2048];
        "CD001"u8.CopyTo(bytes.AsSpan(16 * 2048 + 1)); bytes[16 * 2048] = 1; bytes[16 * 2048 + 6] = 1;
        await File.WriteAllBytesAsync(iso, bytes);
        var repository = new JsonVmRepository(Path.Combine(_root, "library"));
        var request = new VmCreationRequest(VmConfiguration.CreateMetadata("Integration fixture"), 1, iso, _tool, true);
        var snapshot = await new VmCreationService(repository, new QemuImgDiskService()).CreateAsync(request);
        string diskPath = Path.Combine(_root, "library", snapshot.Configuration.Id.ToString("D"), "disks", "system.qcow2");
        Assert.IsTrue(new FileInfo(diskPath).Length < 10 * 1024 * 1024, "A new sparse image should use little physical file data.");
        using (var disk = new FileStream(diskPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            byte[] header = new byte[32]; disk.ReadExactly(header);
            Assert.IsTrue(header.AsSpan(0, 4).SequenceEqual(new byte[] { 0x51, 0x46, 0x49, 0xFB }));
            Assert.AreEqual(1073741824UL, System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(24, 8)));
        }
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(iso));
        Assert.AreEqual(0, (await repository.ListAsync()).Issues.Count);
        await repository.RemoveAsync(snapshot.Configuration.Id);
        Assert.IsTrue(File.Exists(diskPath));
        await repository.RegisterAsync(snapshot.Configuration.Id);
        Assert.AreEqual(1, (await repository.ListAsync()).Machines.Count);
    }

    [TestMethod]
    public async Task RealDiskCollisionAndUnretainedLeasePreserveOwnershipRules()
    {
        string path = Path.Combine(_root, "new disk.qcow2");
        var service = new QemuImgDiskService();
        await using (var lease = await service.CreateAsync(_tool, path, 1073741824)) Assert.IsTrue(File.Exists(path));
        Assert.IsFalse(File.Exists(path));
        await File.WriteAllTextAsync(path, "existing disk sentinel");
        await Assert.ThrowsAsync<IOException>(() => service.CreateAsync(_tool, path, 1073741824));
        Assert.AreEqual("existing disk sentinel", await File.ReadAllTextAsync(path));
    }

    [TestMethod]
    public async Task CancelingOwnedToolTerminatesItBeforeDiskCleanup()
    {
        string path = Path.Combine(_root, "cancel fixture.qcow2");
        await using (var lease = await new QemuImgDiskService().CreateAsync(_tool, path, 1073741824))
        {
            using var child = OwnedToolProcess.Start(_tool, ["bench", "-c", "1000000000", "-s", "4096", "-f", "qcow2", path], Path.GetDirectoryName(_tool)!);
            using var cancellation = new CancellationTokenSource();
            bool cancelDelivered = false;
            using var registration = cancellation.Token.Register(() => { child.CloseJob(); cancelDelivered = true; });
            child.Resume();
            cancellation.CancelAfter(50);
            await Assert.ThrowsAsync<OperationCanceledException>(() => child.WaitForExitAsync(cancellation.Token));
            await child.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(cancelDelivered);
        }
        Assert.IsFalse(File.Exists(path));
    }
}
