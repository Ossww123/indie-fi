using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using IndieFi.Web.Data;
using Microsoft.AspNetCore.DataProtection;

namespace IndieFi.Web.Infrastructure.Authentication;

public static class DataProtectionServiceCollectionExtensions
{
    private const string ApplicationName = "IndieFi.Web";

    public static IServiceCollection AddIndieFiDataProtection(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var builder = services
            .AddDataProtection()
            .SetApplicationName(ApplicationName);

        if (!environment.IsProduction())
        {
            return services;
        }

        var options = configuration
            .GetSection(DataProtectionKeyManagementOptions.SectionName)
            .Get<DataProtectionKeyManagementOptions>()
            ?? new DataProtectionKeyManagementOptions();
        ValidateKeyLifetime(options.KeyLifetimeDays);

        var current = LoadCertificate(
            options.CertificatePath,
            options.PrivateKeyPath,
            "current");
        var previous = options.PreviousCertificates
            .Select((certificate, index) => LoadCertificate(
                certificate.CertificatePath,
                certificate.PrivateKeyPath,
                $"previous certificate {index}"))
            .ToArray();
        RejectDuplicateCertificates(current, previous);

        var certificateSet = new DataProtectionCertificateSet(current, previous);
        services.AddSingleton(certificateSet);

        builder
            .PersistKeysToDbContext<AppDbContext>()
            .ProtectKeysWithCertificate(current)
            .SetDefaultKeyLifetime(TimeSpan.FromDays(options.KeyLifetimeDays));

        if (previous.Length > 0)
        {
            builder.UnprotectKeysWithAnyCertificate(previous);
        }

        return services;
    }

    private static X509Certificate2 LoadCertificate(
        string? certificatePath,
        string? privateKeyPath,
        string description)
    {
        if (string.IsNullOrWhiteSpace(certificatePath)
            || string.IsNullOrWhiteSpace(privateKeyPath))
        {
            throw new InvalidOperationException(
                $"A certificate and private key path are required for the {description} " +
                "Data Protection certificate in production.");
        }

        if (!Path.IsPathFullyQualified(certificatePath)
            || !Path.IsPathFullyQualified(privateKeyPath))
        {
            throw new InvalidOperationException(
                $"The {description} Data Protection certificate paths must be absolute.");
        }

        try
        {
            var certificate = X509Certificate2.CreateFromPemFile(
                certificatePath,
                privateKeyPath);
            if (!certificate.HasPrivateKey
                || certificate.GetRSAPrivateKey() is null)
            {
                certificate.Dispose();
                throw new InvalidOperationException(
                    $"The {description} Data Protection certificate must have an RSA private key.");
            }

            var now = DateTime.UtcNow;
            if (now < certificate.NotBefore.ToUniversalTime()
                || now >= certificate.NotAfter.ToUniversalTime())
            {
                certificate.Dispose();
                throw new InvalidOperationException(
                    $"The {description} Data Protection certificate is not currently valid.");
            }

            ValidatePrivateKeyPermissions(privateKeyPath, description);
            return certificate;
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or CryptographicException)
        {
            throw new InvalidOperationException(
                $"The {description} Data Protection certificate could not be loaded.",
                exception);
        }
    }

    private static void ValidatePrivateKeyPermissions(
        string privateKeyPath,
        string description)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var mode = File.GetUnixFileMode(privateKeyPath);
        const UnixFileMode exposed =
            UnixFileMode.GroupRead
            | UnixFileMode.GroupWrite
            | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead
            | UnixFileMode.OtherWrite
            | UnixFileMode.OtherExecute;
        if ((mode & exposed) != 0)
        {
            throw new InvalidOperationException(
                $"The {description} Data Protection private key must not be accessible " +
                "to group or other users.");
        }
    }

    private static void ValidateKeyLifetime(int keyLifetimeDays)
    {
        if (keyLifetimeDays is < 7 or > 365)
        {
            throw new InvalidOperationException(
                "DataProtectionKeyManagement:KeyLifetimeDays must be between 7 and 365.");
        }
    }

    private static void RejectDuplicateCertificates(
        X509Certificate2 current,
        IReadOnlyList<X509Certificate2> previous)
    {
        var thumbprints = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            current.Thumbprint,
        };

        foreach (var certificate in previous)
        {
            if (!thumbprints.Add(certificate.Thumbprint))
            {
                current.Dispose();
                foreach (var item in previous)
                {
                    item.Dispose();
                }

                throw new InvalidOperationException(
                    "Data Protection certificate entries must not contain a duplicate.");
            }
        }
    }

    private sealed class DataProtectionCertificateSet : IDisposable
    {
        private readonly IReadOnlyList<X509Certificate2> _certificates;

        public DataProtectionCertificateSet(
            X509Certificate2 current,
            IReadOnlyList<X509Certificate2> previous)
        {
            _certificates = [current, .. previous];
        }

        public void Dispose()
        {
            foreach (var certificate in _certificates)
            {
                certificate.Dispose();
            }
        }
    }
}
