namespace IndieFi.Web.Infrastructure.Authentication;

public sealed class DataProtectionKeyManagementOptions
{
    public const string SectionName = "DataProtectionKeyManagement";

    public string? CertificatePath { get; set; }
    public string? PrivateKeyPath { get; set; }
    public int KeyLifetimeDays { get; set; } = 90;
    public List<DataProtectionCertificateOptions> PreviousCertificates { get; set; } = [];
}

public sealed class DataProtectionCertificateOptions
{
    public string? CertificatePath { get; set; }
    public string? PrivateKeyPath { get; set; }
}
