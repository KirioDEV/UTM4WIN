namespace UTM4WIN.Infrastructure;

internal sealed class AtomicFileWriter {
    // An internal commit boundary permits deterministic interruption tests without affecting user data.
    internal Action<string>? BeforeCommit {
        get;
        init;
    }

    public async Task WriteAsync(string destination, byte[] bytes, CancellationToken cancellationToken) {
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        PathSafety.Check(destination);
        PathSafety.Check(destination + ".previous");
        bool owned = false;
        try {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             4096, FileOptions.Asynchronous)) {
                owned = true;
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            BeforeCommit?.Invoke(destination);
            cancellationToken.ThrowIfCancellationRequested();
            PathSafety.Check(destination);
            PathSafety.Check(destination + ".previous");
            if (File.Exists(destination))
                File.Replace(temporary, destination, destination + ".previous");
            else
                File.Move(temporary, destination, overwrite: false);
            owned = false;
        } finally {
            if (owned) {
                PathSafety.Check(temporary);
                File.Delete(temporary);
            }
        }
    }
}