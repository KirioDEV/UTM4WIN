using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
namespace UTM4WIN.Virtualization.Qemu.Display;

internal sealed class DisplayCertificates : IDisposable {
    public X509Certificate2 Authority {
        get;
    }
    public X509Certificate2 Server {
        get;
    }
    public X509Certificate2 Client {
        get;
    }
    public string? ClientKeyName {
        get;
    }
    public CngProvider? ClientKeyProvider {
        get;
    }
    public DisplayCertificates(string directory) {
        try {
            using var caKey = RSA.Create(3072);
            var request = new CertificateRequest("CN=UTM4WIN temporary session CA", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
            Authority = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(8));
            Server = Issue("localhost", "1.3.6.1.5.5.7.3.1", true);
            Client = Issue("UTM4WIN session client", "1.3.6.1.5.5.7.3.2", false);
            using (var clientKey = Client.GetRSAPrivateKey()) {
                if (clientKey is RSACng cng) {
                    ClientKeyName = cng.Key.KeyName;
                    ClientKeyProvider = cng.Key.Provider;
                }
            }
            File.WriteAllText(Path.Combine(directory, "ca-cert.pem"), Authority.ExportCertificatePem());
            File.WriteAllText(Path.Combine(directory, "server-cert.pem"), Server.ExportCertificatePem());
            using var serverKey = Server.GetRSAPrivateKey()!;
            File.WriteAllText(Path.Combine(directory, "server-key.pem"), serverKey.ExportPkcs8PrivateKeyPem());
        } catch { Dispose(); throw; }
    }
    private X509Certificate2 Issue(string name, string usage, bool server) {
        using var key = RSA.Create(3072);
        var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid(usage) }, true));
        if (server) {
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("localhost");
            request.CertificateExtensions.Add(san.Build());
        }
        using var issued = request.Create(Authority, DateTimeOffset.UtcNow.AddMinutes(-2), DateTimeOffset.UtcNow.AddDays(7), RandomNumberGenerator.GetBytes(16));
        using var keyed = issued.CopyWithPrivateKey(key);
        // Schannel cannot use an ephemeral imported private key. The default
        // lifetime deletes this temporary user key container on certificate disposal.
        return server ? issued.CopyWithPrivateKey(key) :
            X509CertificateLoader.LoadPkcs12(keyed.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.UserKeySet);
    }
    public void Dispose() {
        Client?.Dispose();
        Server?.Dispose();
        Authority?.Dispose();
    }
}


