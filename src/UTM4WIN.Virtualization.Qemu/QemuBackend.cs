using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UTM4WIN.Application;
using UTM4WIN.Domain;
using UTM4WIN.Virtualization.Qemu.Display;

namespace UTM4WIN.Virtualization.Qemu;

public sealed class QemuBackend : IVmBackend {
    private readonly Action<uint>? _beforeResume;
    private readonly Action<uint>? _beforeContinue;
    public QemuBackend() {
    }
    internal QemuBackend(Action<uint>? beforeResume, Action<uint>? beforeContinue = null) {
        _beforeResume = beforeResume;
        _beforeContinue = beforeContinue;
    }
    public const string QualifiedSha256 = "47D57A6072E0BB3BD98F87926EB129EB1736DFE818C67B3B81EF7CE4EDD0B3CD";
    public const string QualifiedMachine = "pc-i440fx-9.2";

    public Task<IVmBackendSession> StartAsync(IVmExecutionLease lease, VmStartOptions options, CancellationToken cancellationToken) => Task.Run<IVmBackendSession>(async () => {
        var guards = new List<FileStream>();
        Session? session = null;
        OwnedToolProcess? child = null;
        DisplayMaterial? displayMaterial = null;
        try {
            string executable = options.Executable;
            HostCapabilities.Check(lease.Snapshot.Configuration);
            RequireRuntimePath(executable);
            foreach (var resource in lease.Resources)
                RequireRuntimePath(resource.FullPath);
            QemuImgDiskService.CheckPath(executable);
            if (!Path.GetFileName(executable).Equals("qemu-system-x86_64.exe", StringComparison.OrdinalIgnoreCase))
                throw new ConfigurationException("Choose the qualified qemu-system-x86_64.exe.");
            async Task PinAsync(string path, string expectedHash) {
                QemuImgDiskService.CheckPath(path);
                var guard = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
                guards.Add(guard);
                if (Convert.ToHexString(await SHA256.HashDataAsync(guard, cancellationToken)) != expectedHash)
                    throw new ConfigurationException("This QEMU build is not qualified. Select the approved QEMU 11.1.0 installation.");
            }
            await PinAsync(executable, QualifiedSha256);
            string imageTool = Path.Combine(Path.GetDirectoryName(executable)!, "qemu-img.exe");
            await PinAsync(imageTool, QemuImgDiskService.QualifiedSha256);
            var disk = lease.Resources.Single(resource => resource.Configuration.Format == DiskFormat.Qcow2);
            string json = await QemuImgDiskService.RunAsync(imageTool, ["info", "-f", "qcow2", "--output=json", disk.FullPath], cancellationToken);
            using (var info = JsonDocument.Parse(json)) {
                long size = info.RootElement.GetProperty("virtual-size").GetInt64();
                if (size is < 1073741824 or > 2199023255552)
                    throw new ConfigurationException("Disk capacity is outside the qualified profile.");
                QemuImgDiskService.ValidateInfo(json, size);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var arguments = BuildArguments(lease, options).ToList();
            if (options.IntegratedDisplay) {
                displayMaterial = new();
                arguments.AddRange(["-device", "qemu-xhci,id=consoleusb", "-device", "usb-tablet,bus=consoleusb.0",
                    "-object", "tls-creds-x509,id=consoleTLS,endpoint=server,verify-peer=on,dir=" + displayMaterial.Directory.Replace('\\', '/').Replace(",", ",,", StringComparison.Ordinal),
                    "-vnc", "127.0.0.1:" + (displayMaterial.Port - 5900) + ",tls-creds=consoleTLS,share=ignore"]);
            }
            child = OwnedToolProcess.Start(executable, arguments, Path.GetDirectoryName(executable)!);
            session = new Session(child, guards, displayMaterial);
            using var cancel = cancellationToken.Register(child.CloseJob);
            _beforeResume?.Invoke(child.ProcessId);
            cancellationToken.ThrowIfCancellationRequested();
            child.Resume(keepInputOpen: true);
            await session.InitializeAsync(cancellationToken, _beforeContinue);
            cancellationToken.ThrowIfCancellationRequested();
            return session;
        } catch (Exception error) {
            string diagnostics = session?.Diagnostics ?? "";
            try {
                if (session is not null) {
                    await session.ForceStopAsync(CancellationToken.None);
                    await session.DisposeAsync();
                } else {
                    child?.Dispose();
                    displayMaterial?.Dispose();
                }
            } catch (Exception cleanup) { throw new VmCleanupException("Startup failed and child exit was not confirmed: " + cleanup.Message); }
            foreach (var guard in guards)
                guard.Dispose();
            if (error is OperationCanceledException or ConfigurationException)
                throw;
            throw new IOException("QEMU startup failed: " + error.Message + " " + diagnostics, error);
        }
    }, cancellationToken);

    internal static void RequireRuntimePath(string path) {
        if (path.Any(character => character > 127))
            throw new ConfigurationException("The qualified Windows QEMU system build cannot open non-ASCII resource paths. Use an ASCII-only library, ISO and QEMU installation path; no files were moved or modified.");
    }

    internal static string[] BuildArguments(IVmExecutionLease lease, VmStartOptions options) {
        if (options.IntegratedDisplay && options.ExternalDisplay)
            throw new ConfigurationException("Choose either the integrated console or an external display.");
        var configuration = lease.Snapshot.Configuration;
        var disk = lease.Resources.Single(resource => resource.Configuration.Format == DiskFormat.Qcow2);
        string BlockDevice(string node, string path, string format, bool readOnly) => JsonSerializer.Serialize(new Dictionary<string, object> {
            ["node-name"] = node,
            ["driver"] = format,
            ["read-only"] = readOnly,
            ["file"] = new Dictionary<string, object> { ["driver"] = "file", ["filename"] = path, ["read-only"] = readOnly }
        });
        var arguments = new List<string> {
            "-machine", QualifiedMachine, "-accel", configuration.ExecutionMode == ExecutionMode.Whpx ? "whpx" : "tcg",
            "-m", configuration.MemoryMiB.ToString(CultureInfo.InvariantCulture), "-smp", configuration.CpuCount.ToString(CultureInfo.InvariantCulture),
            "-S", "-display", options.ExternalDisplay ? "sdl" : "none", "-monitor", "none", "-qmp", "stdio", "-nic", "none",
            "-serial", "none", "-parallel", "none", "-no-shutdown", "-name", configuration.Name.Replace(",", ",,", StringComparison.Ordinal),
            "-blockdev", BlockDevice("systemdisk", disk.FullPath, "qcow2", false), "-device", "virtio-blk-pci,drive=systemdisk"
        };
        var iso = lease.Resources.FirstOrDefault(resource => resource.Configuration.Format == DiskFormat.Iso);
        if (iso is not null) {
            arguments.AddRange(["-blockdev", BlockDevice("installation", iso.FullPath, "raw", true), "-device", "ide-cd,drive=installation,bus=ide.1"]);
        }
        if (options.BootInstallationMedia && iso is null)
            throw new ConfigurationException("No installation ISO is configured. Disable installation-media boot to start the disk.");
        arguments.AddRange(["-boot", options.BootInstallationMedia ? "order=c,once=d" : "order=c"]);
        return arguments.ToArray();
    }

    private sealed class Session : IVmBackendSession {
        private readonly OwnedToolProcess _child;
        private readonly List<FileStream> _guards;
        private readonly QmpClient _qmp;
        private readonly Task _diagnostics;
        private readonly StringBuilder _tail = new();
        private bool _disposed;
        private readonly DisplayMaterial? _displayMaterial;
        private readonly VncDisplaySession? _display;
        public IVmDisplaySession? Display => _display;
        public uint ProcessId => _child.ProcessId;
        public Task<uint> Completion {
            get;
        }
        public string Diagnostics {
            get {
                lock (_tail)
                    return _tail.ToString();
            }
        }

        public Session(OwnedToolProcess child, List<FileStream> guards, DisplayMaterial? displayMaterial) {
            _child = child;
            _guards = guards;
            _displayMaterial = displayMaterial;
            if (displayMaterial is not null)
                _display = new(displayMaterial);
            _qmp = new(child.Output, child.Input);
            _diagnostics = Task.Run(async () => {
                char[] buffer = new char[2048];
                try {
                    int count;
                    while ((count = await child.Error.ReadAsync(buffer)) != 0) {
                        lock (_tail) {
                            _tail.Append(buffer, 0, count);
                            if (_tail.Length > 65536)
                                _tail.Remove(0, _tail.Length - 65536);
                        }
                    }
                } catch (IOException) { }
            });
            Completion = Task.Run(async () => {
                try {
                    await child.WaitForExitAsync(CancellationToken.None);
                    return child.ExitCode;
                } catch (Win32Exception error) { throw new IOException("QEMU process exit observation failed.", error); }
            });
        }

        public async Task InitializeAsync(CancellationToken cancellationToken, Action<uint>? beforeContinue) {
            await _qmp.NegotiateAsync(cancellationToken);
            var commands = await _qmp.ExecuteAsync("query-commands", cancellationToken);
            var supported = commands.EnumerateArray().Select(command => command.GetProperty("name").GetString()).ToHashSet();
            foreach (string command in new[] { "query-status", "stop", "cont", "system_powerdown", "quit" })
                if (!supported.Contains(command))
                    throw new IOException("QEMU lacks required command " + command);
            beforeContinue?.Invoke(ProcessId);
            cancellationToken.ThrowIfCancellationRequested();
            if (_display is not null)
                await _display.ReconnectAsync(cancellationToken);
            await _qmp.ExecuteAsync("cont", cancellationToken);
        }

        public async Task<string> QueryStatusAsync(CancellationToken cancellationToken) {
            var response = await _qmp.ExecuteAsync("query-status", cancellationToken);
            try {
                return response.GetProperty("status").GetString() ?? throw new IOException("QEMU returned no state.");
            } catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException) { throw new IOException("QEMU returned an invalid state response.", error); }
        }

        public async Task ExecuteAsync(VmControl control, CancellationToken cancellationToken) {
            if (control == VmControl.Quit && _display is not null) {
                await _display.EndAsync();
                var vnc = await _qmp.ExecuteAsync("query-vnc", cancellationToken);
                if (vnc.TryGetProperty("clients", out var clients) && clients.GetArrayLength() != 0)
                    throw new IOException("Display clients remain connected; QEMU still owns its resources.");
            }
            _ = await _qmp.ExecuteAsync(control switch {
                VmControl.Pause => "stop",
                VmControl.Resume => "cont",
                VmControl.Shutdown => "system_powerdown",
                VmControl.Quit => "quit",
                _ => throw new ArgumentOutOfRangeException(nameof(control))
            }, cancellationToken);
        }

        public async Task ForceStopAsync(CancellationToken cancellationToken) {
            if (_display is not null) {
                try {
                    await _display.EndAsync();
                } catch { /* Explicit force stop still terminates the owned process. */ }
            }
            _child.CloseJob();
            try {
                await Completion.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            } catch (Exception error) when (error is TimeoutException or OperationCanceledException) { throw new VmCleanupException("QEMU exit is not confirmed. Its files remain locked."); }
        }

        public async ValueTask DisposeAsync() {
            if (_disposed)
                return;
            if (!Completion.IsCompleted)
                await ForceStopAsync(CancellationToken.None);
            await Completion;
            await Task.WhenAll(_qmp.ReaderCompletion, _diagnostics).WaitAsync(TimeSpan.FromSeconds(5));
            _child.Dispose();
            if (_display is not null)
                await _display.EndAsync();
            _displayMaterial?.Dispose();
            foreach (var guard in _guards)
                guard.Dispose();
            _guards.Clear();
            _disposed = true;
        }
    }
}
