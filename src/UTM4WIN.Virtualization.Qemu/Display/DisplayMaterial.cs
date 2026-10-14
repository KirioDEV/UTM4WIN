using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Principal;

namespace UTM4WIN.Virtualization.Qemu.Display;

internal sealed class DisplayMaterial : IDisposable {
    private readonly List<FileStream> _guards = [];
    public string Directory {
        get;
    }
    public int Port {
        get;
    }
    public DisplayCertificates Certificates {
        get;
    }

    public DisplayMaterial() {
        Directory = Path.Combine(Path.GetTempPath(), "UTM4WIN-display-" + Guid.NewGuid().ToString("N"));
        QemuBackend.RequireRuntimePath(Directory);
        QemuImgDiskService.CheckPath(Directory);
        System.IO.Directory.CreateDirectory(Directory);
        try {
            var permissions = new DirectorySecurity();
            permissions.SetAccessRuleProtection(true, false);
            permissions.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(Directory).SetAccessControl(permissions);
            Certificates = new(Directory);
            foreach (string name in new[] { "ca-cert.pem", "server-cert.pem", "server-key.pem" })
                _guards.Add(new(Path.Combine(Directory, name), FileMode.Open, FileAccess.Read, FileShare.Read));
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            Port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop(); // A bind race fails startup; certificate pinning prevents connecting to a substitute.
        } catch { Dispose(); throw; }
    }

    public void Dispose() {
        Certificates?.Dispose();
        foreach (var guard in _guards)
            guard.Dispose();
        _guards.Clear();
        QemuImgDiskService.CheckPath(Directory);
        foreach (string name in new[] { "ca-cert.pem", "server-cert.pem", "server-key.pem" })
            File.Delete(Path.Combine(Directory, name));
        if (!System.IO.Directory.EnumerateFileSystemEntries(Directory).Any())
            System.IO.Directory.Delete(Directory);
    }
}
