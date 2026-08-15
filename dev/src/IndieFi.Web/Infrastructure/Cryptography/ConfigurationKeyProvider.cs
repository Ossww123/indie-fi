using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;

namespace IndieFi.Web.Infrastructure.Cryptography;

public sealed class ConfigurationKeyProvider : IKeyProvider
{
    private const string RootSection = "AccountProtection:Purposes";
    private const int MaxKeyIdLength = 100;

    private readonly IReadOnlyDictionary<KeyPurpose, PurposeKeys> _purposes;

    public ConfigurationKeyProvider(IConfiguration configuration)
        : this(configuration.GetSection(RootSection))
    {
    }

    internal ConfigurationKeyProvider(IConfigurationSection purposesSection)
    {
        _purposes = new Dictionary<KeyPurpose, PurposeKeys>
        {
            [KeyPurpose.AccountEmailEncryption] = LoadPurpose(
                purposesSection,
                KeyPurpose.AccountEmailEncryption),
            [KeyPurpose.AccountEmailLookup] = LoadPurpose(
                purposesSection,
                KeyPurpose.AccountEmailLookup),
            [KeyPurpose.AccountEmailMessageEncryption] = LoadPurpose(
                purposesSection,
                KeyPurpose.AccountEmailMessageEncryption),
            [KeyPurpose.AccountRequestIpLookup] = LoadPurpose(
                purposesSection,
                KeyPurpose.AccountRequestIpLookup),
        };

        RejectSharedSecrets(_purposes.Values);
    }

    public KeyMaterial GetCurrent(KeyPurpose purpose)
    {
        var keys = GetPurpose(purpose);
        return keys.Keys[keys.CurrentKeyId];
    }

    public KeyMaterial GetById(KeyPurpose purpose, string keyId)
    {
        var keys = GetPurpose(purpose);
        if (!keys.Keys.TryGetValue(keyId, out var key))
        {
            throw new InvalidOperationException(
                $"Key '{keyId}' is not configured for purpose '{purpose}'.");
        }

        return key;
    }

    public IReadOnlyList<KeyMaterial> GetReadable(KeyPurpose purpose)
    {
        var keys = GetPurpose(purpose);
        return keys.ReadKeyIds.Select(id => keys.Keys[id]).ToArray();
    }

    public IReadOnlyList<KeyMaterial> GetWritable(KeyPurpose purpose)
    {
        var keys = GetPurpose(purpose);
        return keys.WriteKeyIds.Select(id => keys.Keys[id]).ToArray();
    }

    private PurposeKeys GetPurpose(KeyPurpose purpose)
    {
        if (!_purposes.TryGetValue(purpose, out var keys))
        {
            throw new InvalidOperationException(
                $"Key purpose '{purpose}' is not configured.");
        }

        return keys;
    }

    private static PurposeKeys LoadPurpose(
        IConfigurationSection purposesSection,
        KeyPurpose purpose)
    {
        var section = purposesSection.GetSection(purpose.Value);
        var currentKeyId = RequireValue(section, "CurrentKeyId", purpose);
        var readKeyIds = RequireDistinctList(section, "ReadKeyIds", purpose);
        var writeKeyIds = RequireDistinctList(section, "WriteKeyIds", purpose);

        var requiredIds = readKeyIds
            .Concat(writeKeyIds)
            .Append(currentKeyId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (requiredIds.Any(keyId => keyId.Length > MaxKeyIdLength))
        {
            throw new InvalidOperationException(
                $"Key IDs for purpose '{purpose}' must be {MaxKeyIdLength} characters or fewer.");
        }

        var keys = new Dictionary<string, KeyMaterial>(StringComparer.Ordinal);

        foreach (var keyId in requiredIds)
        {
            var encodedSecret = section[$"Keys:{keyId}"];
            if (string.IsNullOrWhiteSpace(encodedSecret))
            {
                throw new InvalidOperationException(
                    $"Key '{keyId}' is missing for purpose '{purpose}'.");
            }

            byte[] secret;
            try
            {
                secret = Convert.FromBase64String(encodedSecret);
            }
            catch (FormatException exception)
            {
                throw new InvalidOperationException(
                    $"Key '{keyId}' for purpose '{purpose}' is not valid Base64.",
                    exception);
            }

            if (secret.Length != 32)
            {
                CryptographicOperations.ZeroMemory(secret);
                throw new InvalidOperationException(
                    $"Key '{keyId}' for purpose '{purpose}' must contain 32 bytes.");
            }

            keys.Add(keyId, new KeyMaterial(keyId, secret));
            CryptographicOperations.ZeroMemory(secret);
        }

        if (!writeKeyIds.Contains(currentKeyId, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Current key '{currentKeyId}' for purpose '{purpose}' must be writable.");
        }

        if (!readKeyIds.Contains(currentKeyId, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Current key '{currentKeyId}' for purpose '{purpose}' must be readable.");
        }

        return new PurposeKeys(
            currentKeyId,
            keys,
            readKeyIds,
            writeKeyIds);
    }

    private static string RequireValue(
        IConfigurationSection section,
        string name,
        KeyPurpose purpose)
    {
        var value = section[name];
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"{name} is required for key purpose '{purpose}'.");
        }

        return value;
    }

    private static string[] RequireDistinctList(
        IConfigurationSection section,
        string name,
        KeyPurpose purpose)
    {
        var values = section
            .GetSection(name)
            .GetChildren()
            .Select(item => item.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToArray();

        if (values.Length == 0)
        {
            throw new InvalidOperationException(
                $"At least one {name} entry is required for key purpose '{purpose}'.");
        }

        if (values.Distinct(StringComparer.Ordinal).Count() != values.Length)
        {
            throw new InvalidOperationException(
                $"{name} for key purpose '{purpose}' contains a duplicate key ID.");
        }

        return values;
    }

    private static void RejectSharedSecrets(IEnumerable<PurposeKeys> purposes)
    {
        var purposeList = purposes.ToArray();
        for (var firstIndex = 0; firstIndex < purposeList.Length; firstIndex++)
        {
            for (var secondIndex = firstIndex + 1;
                 secondIndex < purposeList.Length;
                 secondIndex++)
            {
                foreach (var firstKey in purposeList[firstIndex].Keys.Values)
                {
                    foreach (var secondKey in purposeList[secondIndex].Keys.Values)
                    {
                        if (CryptographicOperations.FixedTimeEquals(
                                firstKey.Secret.Span,
                                secondKey.Secret.Span))
                        {
                            throw new InvalidOperationException(
                                "Account protection purposes must not share key material.");
                        }
                    }
                }
            }
        }
    }

    private sealed record PurposeKeys(
        string CurrentKeyId,
        IReadOnlyDictionary<string, KeyMaterial> Keys,
        IReadOnlyList<string> ReadKeyIds,
        IReadOnlyList<string> WriteKeyIds);
}
