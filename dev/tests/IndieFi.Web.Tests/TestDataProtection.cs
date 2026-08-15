using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace IndieFi.Web.Tests;

internal static class TestDataProtection
{
    private static readonly Lazy<CertificateFiles> SharedFiles = new(CreateFiles);

    public static string CertificatePath => SharedFiles.Value.CertificatePath;
    public static string PrivateKeyPath => SharedFiles.Value.PrivateKeyPath;

    private static CertificateFiles CreateFiles()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"indiefi-data-protection-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var certificatePath = Path.Combine(directory, "certificate.pem");
        var privateKeyPath = Path.Combine(directory, "private-key.pem");

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=IndieFi Test Data Protection",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DataEncipherment,
            critical: true));
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(1));

        File.WriteAllText(certificatePath, certificate.ExportCertificatePem());
        File.WriteAllText(privateKeyPath, rsa.ExportPkcs8PrivateKeyPem());
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(
                privateKeyPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return new CertificateFiles(certificatePath, privateKeyPath);
    }

    private sealed record CertificateFiles(
        string CertificatePath,
        string PrivateKeyPath);
}
