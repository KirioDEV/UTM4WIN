using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using UTM4WIN.Application;
using UTM4WIN.Domain;
using UTM4WIN.Infrastructure;
using UTM4WIN.Virtualization.Qemu;

namespace UTM4WIN.UnitTests;

[TestClass]
[TestCategory("QemuIntegration")]
public sealed class QemuNetworkInstallIntegrationTests {
    private const uint Escape = 0xff1b, Enter = 0xff0d, LeftShift = 0xffe1;

    // Installs a new disposable guest through the production launch profile and console input.
    // The guest reaches a loopback-only test server through the NAT gateway and makes one outbound HTTP request.
    [TestMethod]
    [Timeout(1800000)]
    public async Task CreateInstallRebootPauseShutdownAndRestartPersistedGuestOverNat() {
        string executable = Environment.GetEnvironmentVariable("UTM4WIN_TEST_QEMU_SYSTEM") ?? "";
        string iso = Environment.GetEnvironmentVariable("UTM4WIN_TEST_ISO") ?? "";
        if (Environment.GetEnvironmentVariable("UTM4WIN_TEST_NETWORK_INSTALL") != "1" || !File.Exists(executable) || !File.Exists(iso))
            Assert.Inconclusive("Set UTM4WIN_TEST_NETWORK_INSTALL=1 with the approved QEMU binary and Debian installation ISO to run the unattended NAT installation.");
        string root = Path.Combine(Path.GetTempPath(), "UTM4WIN-network install " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(25));
        await using var server = new GuestReportServer();
        try {
            Guid id;
            {
                var repository = new JsonVmRepository(root);
                var snapshot = await new VmCreationService(repository, new QemuImgDiskService()).CreateAsync(new(
                    VmConfiguration.CreateMetadata("Network install fixture") with {
                        MemoryMiB = 1024,
                        CpuCount = 2,
                        Network = NetworkMode.User
                    }, 4, iso, Path.Combine(Path.GetDirectoryName(executable)!, "qemu-img.exe"), true));
                id = snapshot.Configuration.Id;
                await using var sessions = new VmSessionService(repository, new QemuBackend());
                await sessions.StartAsync(snapshot, new(executable, false, true, true), deadline.Token);
                var display = sessions.GetDisplay(id)!;
                try {
                    // The Debian BIOS boot menu switches the firmware text mode to 640 × 480.
                    while (display.ReadFrame() is not { Width: 640, Height: 480 })
                        await Task.Delay(100, deadline.Token);
                    await Task.Delay(2000, deadline.Token);
                    await TypeAsync(display, [Escape], deadline.Token);
                    await Task.Delay(1000, deadline.Token);
                    await TypeAsync(display, ("auto url=http://10.0.2.2:" + server.Port + "/preseed.cfg").Select(character => (uint)character), deadline.Token);
                    await TypeAsync(display, [Enter], deadline.Token);
                    await display.ReleaseInputAsync();
                    string installed = await server.WaitAsync("/installed", TimeSpan.FromMinutes(15), deadline.Token);
                    StringAssert.Contains(installed, "outbound=ok", "The installer reached the host through NAT, but its outbound HTTP request failed.");
                    string firstBoot = await server.WaitAsync("/boot", TimeSpan.FromMinutes(5), deadline.Token);
                    StringAssert.Contains(firstBoot, "count=1&acpid=active");
                } catch {
                    Capture(display, "failure");
                    throw;
                }
                Capture(display, "installed-first-boot");
                Assert.AreEqual(VmSessionState.Running, sessions.Get(id).State);
                await sessions.ControlAsync(id, VmControl.Pause, deadline.Token);
                Assert.AreEqual(VmSessionState.Paused, sessions.Get(id).State);
                await sessions.ControlAsync(id, VmControl.Resume, deadline.Token);
                Assert.AreEqual(VmSessionState.Running, sessions.Get(id).State);
                await ShutDownAsync(sessions, id, deadline.Token);
            }
            {
                // Equivalent to an application restart: new repository and session service over the persisted library.
                var repository = new JsonVmRepository(root);
                var snapshot = (await repository.ListAsync(deadline.Token)).Machines.Single(machine => machine.Configuration.Id == id);
                Assert.AreEqual(NetworkMode.User, snapshot.Configuration.Network);
                await using var sessions = new VmSessionService(repository, new QemuBackend());
                await sessions.StartAsync(snapshot, new(executable, false, false, true), deadline.Token);
                string secondBoot = await server.WaitAsync("/boot", TimeSpan.FromMinutes(5), deadline.Token);
                StringAssert.Contains(secondBoot, "count=2&acpid=active");
                await ShutDownAsync(sessions, id, deadline.Token);
            }
        } finally {
            if (Path.GetDirectoryName(root) == Path.TrimEndingDirectorySeparator(Path.GetTempPath()) && Path.GetFileName(root).StartsWith("UTM4WIN-network install ", StringComparison.Ordinal))
                Directory.Delete(root, true);
        }
    }

    private static async Task ShutDownAsync(VmSessionService sessions, Guid id, CancellationToken cancellationToken) {
        await sessions.ControlAsync(id, VmControl.Shutdown, cancellationToken);
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        shutdown.CancelAfter(TimeSpan.FromSeconds(90));
        while (sessions.Get(id).OwnsResources)
            await Task.Delay(100, shutdown.Token);
        Assert.AreEqual(VmSessionState.Stopped, sessions.Get(id).State, sessions.Get(id).Error);
    }

    private static async Task TypeAsync(IVmDisplaySession display, IEnumerable<uint> symbols, CancellationToken cancellationToken) {
        foreach (uint symbol in symbols) {
            bool shifted = symbol == ':';
            if (shifted)
                display.KeyDown(LeftShift);
            display.KeyDown(symbol);
            await Task.Delay(40, cancellationToken);
            display.KeyUp(symbol);
            if (shifted)
                display.KeyUp(LeftShift);
            await Task.Delay(40, cancellationToken);
        }
    }

    // Optional evidence: a 32-bit top-down bitmap of the current console frame.
    private static void Capture(IVmDisplaySession display, string name) {
        string directory = Environment.GetEnvironmentVariable("UTM4WIN_TEST_EVIDENCE") ?? "";
        if (!Directory.Exists(directory) || display.ReadFrame() is not { } frame)
            return;
        using var output = new FileStream(Path.Combine(directory, name + "-" + Guid.NewGuid().ToString("N") + ".bmp"), FileMode.CreateNew);
        using var writer = new BinaryWriter(output);
        writer.Write((byte)'B');
        writer.Write((byte)'M');
        writer.Write(54 + frame.Pixels.Length);
        writer.Write(0);
        writer.Write(54);
        writer.Write(40);
        writer.Write(frame.Width);
        writer.Write(-frame.Height);
        writer.Write((short)1);
        writer.Write((short)32);
        writer.Write(0);
        writer.Write(frame.Pixels.Length);
        writer.Write(0L);
        writer.Write(0L);
        writer.Write(frame.Pixels.Span);
    }

    // Minimal loopback HTTP responder. The guest reaches it at 10.0.2.2 through QEMU's user-mode gateway.
    private sealed class GuestReportServer : IAsyncDisposable {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentQueue<string> _requests = new();
        private readonly Dictionary<string, string> _documents;
        private readonly Task _loop;
        public int Port {
            get;
        }

        public GuestReportServer() {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            string host = "http://10.0.2.2:" + Port;
            _documents = new() {
                ["/preseed.cfg"] = $"""
                    d-i debian-installer/locale string en_US.UTF-8
                    d-i keyboard-configuration/xkb-keymap select us
                    d-i netcfg/choose_interface select auto
                    d-i netcfg/get_hostname string utm4win-test
                    d-i netcfg/get_domain string local
                    d-i hw-detect/load_firmware boolean false
                    d-i clock-setup/utc boolean true
                    d-i time/zone string Etc/UTC
                    d-i clock-setup/ntp boolean false
                    d-i passwd/root-login boolean true
                    d-i passwd/root-password password {password}
                    d-i passwd/root-password-again password {password}
                    d-i passwd/make-user boolean false
                    d-i partman-auto/disk string /dev/vda
                    d-i partman-auto/method string regular
                    d-i partman-auto/choose_recipe select atomic
                    d-i partman-partitioning/confirm_write_new_label boolean true
                    d-i partman/choose_partition select finish
                    d-i partman/confirm boolean true
                    d-i partman/confirm_nooverwrite boolean true
                    d-i base-installer/install-recommends boolean false
                    d-i apt-setup/use_mirror boolean false
                    d-i apt-setup/services-select multiselect
                    d-i apt-setup/cdrom/set-first boolean false
                    d-i pkgsel/run_tasksel boolean false
                    d-i pkgsel/include string acpid acpi-support-base
                    tasksel tasksel/first multiselect
                    d-i pkgsel/upgrade select none
                    popularity-contest popularity-contest/participate boolean false
                    d-i grub-installer/only_debian boolean true
                    d-i grub-installer/bootdev string /dev/vda
                    d-i finish-install/reboot_in_progress note
                    d-i preseed/late_command string wget -q -O /target/usr/local/sbin/utm4win-boot-report {host}/report.sh && wget -q -O /target/etc/systemd/system/utm4win-boot-report.service {host}/report.service && chmod 700 /target/usr/local/sbin/utm4win-boot-report && in-target systemctl enable utm4win-boot-report.service && in-target passwd -l root; result=fail; wget -q -O /dev/null http://deb.debian.org/debian/dists/trixie/InRelease && result=ok; wget -q -O /dev/null "{host}/installed?outbound=$result"

                    """,
                ["/report.sh"] = $"""
                    #!/bin/bash
                    mkdir -p /var/lib/utm4win-test
                    count=0
                    test ! -f /var/lib/utm4win-test/boot-count || read count < /var/lib/utm4win-test/boot-count
                    count=$((count + 1))
                    echo "$count" > /var/lib/utm4win-test/boot-count
                    sync
                    for attempt in $(seq 120); do
                        ( exec 3<>/dev/tcp/10.0.2.2/{Port} && printf 'GET /boot?count=%s&acpid=%s HTTP/1.0\r\n\r\n' "$count" "$(systemctl is-active acpid)" >&3 && cat <&3 > /dev/null ) 2> /dev/null && exit 0
                        sleep 1
                    done
                    exit 1

                    """,
                ["/report.service"] = """
                    [Unit]
                    Description=UTM4WIN disposable network test boot report
                    After=network-online.target acpid.service
                    Wants=network-online.target acpid.service
                    [Service]
                    Type=oneshot
                    ExecStart=/usr/local/sbin/utm4win-boot-report
                    [Install]
                    WantedBy=multi-user.target

                    """
            };
            _loop = Task.Run(async () => {
                try {
                    while (true) {
                        using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                        try {
                            using var limit = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                            limit.CancelAfter(TimeSpan.FromSeconds(10));
                            var stream = client.GetStream();
                            byte[] buffer = new byte[4096];
                            int length = 0;
                            while (length < buffer.Length && !Encoding.ASCII.GetString(buffer, 0, length).Contains("\r\n\r\n", StringComparison.Ordinal)) {
                                int read = await stream.ReadAsync(buffer.AsMemory(length), limit.Token);
                                if (read == 0)
                                    break;
                                length += read;
                            }
                            string[] request = Encoding.ASCII.GetString(buffer, 0, length).Split("\r\n")[0].Split(' ');
                            string target = request.Length > 1 ? request[1] : "";
                            string body = _documents.GetValueOrDefault(target.Split('?')[0], "ok\n").Replace("\r\n", "\n", StringComparison.Ordinal);
                            byte[] response = Encoding.ASCII.GetBytes("HTTP/1.0 200 OK\r\nContent-Type: text/plain\r\nContent-Length: " + Encoding.ASCII.GetByteCount(body) + "\r\nConnection: close\r\n\r\n" + body);
                            await stream.WriteAsync(response, limit.Token);
                            _requests.Enqueue(target);
                        } catch (Exception error) when (error is IOException or SocketException or OperationCanceledException && !_stop.IsCancellationRequested) { }
                    }
                } catch (OperationCanceledException) { }
            });
        }

        // Returns and consumes the next request whose path matches; earlier requests are discarded.
        public async Task<string> WaitAsync(string path, TimeSpan timeout, CancellationToken cancellationToken) {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout);
            try {
                while (true) {
                    while (_requests.TryDequeue(out string? request))
                        if (request.Split('?')[0] == path)
                            return request;
                    await Task.Delay(200, limit.Token);
                }
            } catch (OperationCanceledException) { throw new TimeoutException("The guest did not request " + path + " in time."); }
        }

        public async ValueTask DisposeAsync() {
            _stop.Cancel();
            await _loop;
            _listener.Stop();
            _stop.Dispose();
        }
    }
}
