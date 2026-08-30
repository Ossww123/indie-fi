namespace IndieFi.Seed;

internal sealed record LoadTestCredentials(int SchemaVersion, string Password);

internal sealed record SeedManifest(
    int SchemaVersion,
    string RunId,
    DateTimeOffset CreatedAtUtc,
    string Commit,
    string BaseUrl,
    string Marker,
    SeedAccountManifest Accounts,
    SeedCatalogManifest Catalog,
    SeedDropManifest Drop);

internal sealed record SeedAccountManifest(
    int Total,
    SeedAccountPool Owners,
    SeedAccountPool Claimers,
    SeedAccountPool Latecomers,
    SeedAccountPool Retries);

internal sealed record SeedAccountPool(string Name, int Count, string EmailPrefix);

internal sealed record SeedCatalogManifest(
    int GameCount,
    long FirstGameId,
    long LastGameId,
    IReadOnlyList<long> GameIds);

internal sealed record SeedDropManifest(
    long Id,
    long GameId,
    DateTimeOffset OpensAtUtc,
    int KeyCount);

internal sealed record LoadRunAudit(
    int SchemaVersion,
    string RunId,
    string Phase,
    DateTimeOffset CapturedAtUtc,
    long AccountCount,
    long ConfirmedAccountCount,
    long GameCount,
    long DropCount,
    long InventoryCount,
    long FingerprintCount,
    long GrantCount,
    long DistinctGrantUsers,
    long DistinctGrantKeys,
    long DuplicateGrantUsers,
    long DuplicateGrantKeys,
    long AuthenticationEmailPending,
    long MessagingOutboxPending,
    long NotificationUnresolved,
    long MediaInFlight,
    bool Passed,
    IReadOnlyList<string> Violations);
