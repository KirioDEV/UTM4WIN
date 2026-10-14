using System.Security.Authentication;
using UTM4WIN.Application;
using UTM4WIN.Domain;
using UTM4WIN.Infrastructure;
using UTM4WIN.Virtualization.Qemu;
using UTM4WIN.Virtualization.Qemu.Display;

namespace UTM4WIN.UnitTests;

[TestClass]
[TestCategory("QemuIntegration")]
public sealed class QemuConsoleIntegrationTests {
    [TestMethod]
    public async Task AuthenticatedConsoleReceivesFramesReconnectsAndKeepsLifecycleIndependent() {
        string executable = Environment.GetEnvironmentVariable("UTM4WIN_TEST_QEMU_SYSTEM") ?? "";
        string iso = Environment.GetEnvironmentVariable("UTM4WIN_TEST_ISO") ?? "";
        if (!File.Exists(executable) || !File.Exists(iso))
            Assert.Inconclusive("Select the approved QEMU system executable and disposable-test ISO.");
        string root = Path.Combine(Path.GetTempPath(), "UTM4WIN-console test " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try {
            var repository = new JsonVmRepository(root);
            var snapshot = await new VmCreationService(repository, new QemuImgDiskService()).CreateAsync(new(
                VmConfiguration.CreateMetadata("Console fixture") with {
                    MemoryMiB = 512,
                    CpuCount = 1
                }, 1, iso,
                Path.Combine(Path.GetDirectoryName(executable)!, "qemu-img.exe"), true));
            await using var sessions = new VmSessionService(repository, new QemuBackend());
            await sessions.StartAsync(snapshot, new(executable, false, true, true));
            var display = sessions.GetDisplay(snapshot.Configuration.Id)!;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            while (display.ReadFrame() is null)
                await Task.Delay(50, deadline.Token);
            var frame = display.ReadFrame()!;
            Assert.IsTrue(frame.Width is > 0 and <= 2048);
            Assert.AreEqual(frame.Width * frame.Height * 4, frame.Pixels.Length);
            display.KeyDown(0xff52);
            display.KeyUp(0xff52);
            for (int move = 0; move < 80; move++) {
                display.Pointer((ushort)(move % frame.Width), (ushort)(move % frame.Height), 0);
                await Task.Delay(10, deadline.Token);
            }
            await display.ReleaseInputAsync();
            await Task.Delay(TimeSpan.FromSeconds(35), deadline.Token);
            Assert.IsTrue(display.IsConnected, "The display must remain connected beyond startup and input deadlines: " + display.Error);
            await display.DisconnectAsync();
            Assert.IsFalse(display.IsConnected);
            Assert.AreEqual(VmSessionState.Running, sessions.Get(snapshot.Configuration.Id).State);
            Assert.IsTrue(sessions.Get(snapshot.Configuration.Id).OwnsResources);
            var material = ((VncDisplaySession)display).Material;
            using (var wrongPin = new VncConnection()) {
                await Assert.ThrowsAsync<AuthenticationException>(() => wrongPin.ConnectAsync(material.Port, material.Certificates, true, false, deadline.Token));
                Assert.IsTrue(wrongPin.ServerCertificateRejected);
            }
            using (var missingClient = new VncConnection()) {
                var error = await Assert.ThrowsAsync<Exception>(() => missingClient.ConnectAsync(material.Port, material.Certificates, false, true, deadline.Token));
                Assert.IsFalse(error is OperationCanceledException, "Missing-client rejection must not be inferred from our test deadline.");
            }
            await display.ReconnectAsync(deadline.Token);
            while (display.ReadFrame() is null)
                await Task.Delay(50, deadline.Token);
            await sessions.ControlAsync(snapshot.Configuration.Id, VmControl.Pause);
            Assert.AreEqual(VmSessionState.Paused, sessions.Get(snapshot.Configuration.Id).State);
            await sessions.ForceStopAsync(snapshot.Configuration.Id);
            Assert.IsFalse(display.IsConnected);
            Assert.IsFalse(sessions.Get(snapshot.Configuration.Id).OwnsResources);
            Assert.IsFalse(Directory.Exists(material.Directory), "Normal teardown removes this session's private PEM material.");
        } finally {
            if (Path.GetDirectoryName(root) == Path.TrimEndingDirectorySeparator(Path.GetTempPath()) && Path.GetFileName(root).StartsWith("UTM4WIN-console test ", StringComparison.Ordinal))
                Directory.Delete(root, true);
        }
    }
}
