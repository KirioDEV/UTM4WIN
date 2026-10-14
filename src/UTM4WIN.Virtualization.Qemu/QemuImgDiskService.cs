using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UTM4WIN.Application;

namespace UTM4WIN.Virtualization.Qemu;

public sealed class QemuImgDiskService : IDiskImageService
{
    public const string QualifiedSha256 = "9D930582D0D574BB106D89982C547688E10B8F356192D302CC3D0AE4E8674534";

    public Task<ICreatedDisk> CreateAsync(string executable, string destination, long sizeBytes, CancellationToken cancellationToken = default) => Task.Run<ICreatedDisk>(async () =>
    {
        CheckPath(executable);
        CheckPath(destination);
        if (!Path.GetFileName(executable).Equals("qemu-img.exe", StringComparison.OrdinalIgnoreCase) || sizeBytes is < 1073741824 or > 2199023255552)
            throw new ConfigurationException("A qualified qemu-img.exe and a 1–2048 GiB disk capacity are required.");
        // Pin the selected executable against replacement through qualification and execution.
        await using var executableGuard = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        string hash = Convert.ToHexString(await SHA256.HashDataAsync(executableGuard, cancellationToken));
        if (hash != QualifiedSha256) throw new ConfigurationException("This qemu-img build is not qualified. Select the approved QEMU 11.1.0 tools; no image was created.");
        var version = await RunAsync(executable, ["--version"], cancellationToken);
        if (!version.StartsWith("qemu-img version 11.1.0 ", StringComparison.Ordinal)) throw new ConfigurationException("Unexpected QEMU image-tool version.");
        FileStream? guard = null;
        bool created = false;
        bool safeToDelete = true;
        try
        {
            guard = new(destination, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite);
            created = true;
            await RunAsync(executable, ["create", "-q", "-f", "qcow2", "-o", "compat=1.1,preallocation=off", destination,
                sizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)], cancellationToken);
            string info = await RunAsync(executable, ["info", "-f", "qcow2", "--output=json", destination], cancellationToken);
            ValidateInfo(info, sizeBytes);
            guard.Flush(flushToDisk: true);
            cancellationToken.ThrowIfCancellationRequested();
            var disk = new CreatedDisk(destination, guard);
            guard = null;
            created = false;
            return disk;
        }
        catch (ToolCleanupException) { safeToDelete = false; throw; }
        finally
        {
            guard?.Dispose();
            if (created && safeToDelete) { CheckPath(destination); File.Delete(destination); }
        }
    }, cancellationToken);

    internal static void ValidateInfo(string json, long expectedSize)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowDuplicateProperties = false });
            var root = document.RootElement;
            if (root.GetProperty("format").GetString() != "qcow2" || root.GetProperty("virtual-size").GetInt64() != expectedSize ||
                root.TryGetProperty("backing-filename", out _) || root.TryGetProperty("full-backing-filename", out _) ||
                root.TryGetProperty("encrypted", out var encrypted) && encrypted.GetBoolean())
                throw new ConfigurationException("The new disk has an unexpected format, capacity, backing file or encryption.");
            if (root.TryGetProperty("format-specific", out var specific) && specific.TryGetProperty("data", out var data) &&
                (data.TryGetProperty("data-file", out _) || data.TryGetProperty("encrypt", out _) ||
                    data.TryGetProperty("corrupt", out var corrupt) && corrupt.GetBoolean()))
                throw new ConfigurationException("Corrupt disks, external dependencies and encryption are not supported.");
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { throw new ConfigurationException("The disk tool returned invalid image metadata."); }
    }

    private static async Task<string> RunAsync(string executable, string[] arguments, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var child = OwnedToolProcess.Start(executable, arguments, Path.GetDirectoryName(executable)!);
        using var cancellation = deadline.Token.Register(child.CloseJob);
        deadline.Token.ThrowIfCancellationRequested();
        child.Resume();
        Task<string> output = CaptureAsync(child.Output, deadline);
        Task<string> error = CaptureAsync(child.Error, deadline);
        try
        {
            await Task.WhenAll(output, error, child.WaitForExitAsync(deadline.Token)).WaitAsync(TimeSpan.FromSeconds(35)).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            if (child.ExitCode != 0) throw new IOException($"qemu-img failed ({child.ExitCode}): {await error}");
            return await output;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new IOException("qemu-img exceeded its deadline or output limit; its owned process was stopped."); }
        catch (TimeoutException)
        { throw new ToolCleanupException("Disk-tool output/exit could not be confirmed within cleanup bounds; retain its disk for manual recovery."); }
    }

    private static Task<string> CaptureAsync(StreamReader reader, CancellationTokenSource deadline) => Task.Run(() =>
    {
        var captured = new StringBuilder();
        char[] buffer = new char[2048];
        try
        {
            int count;
            while ((count = reader.Read(buffer, 0, buffer.Length)) != 0)
            {
                if (captured.Length + count > 65536) throw new IOException("Disk-tool output exceeded 64 Ki characters.");
                captured.Append(buffer, 0, count);
            }
            return captured.ToString();
        }
        catch { deadline.Cancel(); throw; }
    });

    private static void CheckPath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ConfigurationException("Local absolute disk-tool and destination paths are required.");
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse points are not supported in disk-tool paths.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private sealed class CreatedDisk(string path, FileStream guard) : ICreatedDisk
    {
        private bool _retained;
        private bool _disposed;
        public void Retain() => _retained = true;
        public ValueTask DisposeAsync()
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            guard.Dispose();
            if (!_retained) { CheckPath(path); File.Delete(path); }
            return ValueTask.CompletedTask;
        }
    }
}
