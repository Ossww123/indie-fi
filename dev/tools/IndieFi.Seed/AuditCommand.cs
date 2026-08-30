using IndieFi.Web.Data;
using IndieFi.Web.Modules.Media.Domain;
using IndieFi.Web.Modules.Notifications.Domain;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Seed;

internal static class AuditCommand
{
    public static async Task<int> RunAsync(
        DbContextOptions<AppDbContext> dbOptions,
        AuditOptions options)
    {
        var manifest = await JsonFile.ReadAsync<SeedManifest>(options.ManifestPath);
        if (manifest.SchemaVersion != 1)
        {
            throw new ArgumentException("Unsupported seed manifest schemaVersion.");
        }
        if (File.Exists(options.OutputPath))
        {
            throw new InvalidOperationException($"Refusing to overwrite audit evidence: {options.OutputPath}");
        }

        await using var db = new AppDbContext(dbOptions);
        var accounts = await db.UserProfiles.AsNoTracking()
            .LongCountAsync(profile => profile.Biography == manifest.Marker);
        var confirmedAccounts = await (
                from profile in db.UserProfiles.AsNoTracking()
                join user in db.Users.AsNoTracking() on profile.UserId equals user.Id
                where profile.Biography == manifest.Marker
                    && user.EmailConfirmedAt != null
                    && user.PasswordHash != null
                select user.Id)
            .LongCountAsync();
        var games = await db.Games.AsNoTracking()
            .LongCountAsync(game => game.Description != null && game.Description.StartsWith(manifest.Marker));
        var drops = await (
                from drop in db.KeyDrops.AsNoTracking()
                join game in db.Games.AsNoTracking() on drop.GameId equals game.Id
                where drop.Id == manifest.Drop.Id
                    && game.Description != null
                    && game.Description.StartsWith(manifest.Marker)
                select drop.Id)
            .LongCountAsync();
        var inventory = await db.GameKeys.AsNoTracking()
            .LongCountAsync(key => key.DropId == manifest.Drop.Id);
        var fingerprints = await (
                from fingerprint in db.GameKeyFingerprints.AsNoTracking()
                join key in db.GameKeys.AsNoTracking() on fingerprint.GameKeyId equals key.Id
                where key.DropId == manifest.Drop.Id
                select fingerprint)
            .LongCountAsync();
        var grants = db.KeyGrants.AsNoTracking().Where(grant => grant.DropId == manifest.Drop.Id);
        var grantCount = await grants.LongCountAsync();
        var distinctUsers = await grants.Select(grant => grant.UserId).Distinct().LongCountAsync();
        var distinctKeys = await grants.Select(grant => grant.KeyId).Distinct().LongCountAsync();
        var duplicateUsers = await grants.GroupBy(grant => grant.UserId)
            .LongCountAsync(group => group.Count() > 1);
        var duplicateKeys = await grants.GroupBy(grant => grant.KeyId)
            .LongCountAsync(group => group.Count() > 1);

        var authenticationEmailPending = await db.AuthenticationEmailOutboxMessages.AsNoTracking().LongCountAsync();
        var messagingOutboxPending = await ScalarAsync(
            db,
            "SELECT count(*)::bigint AS \"Value\" FROM message_outbox_messages");
        var notificationUnresolved = await db.NotificationDeliveries.AsNoTracking()
            .LongCountAsync(delivery => delivery.Status == NotificationDeliveryStatus.Pending
                || delivery.Status == NotificationDeliveryStatus.Failed);
        var mediaInFlight = await db.MediaAssets.AsNoTracking()
            .LongCountAsync(media => media.Status == MediaStatus.UploadPending
                || media.Status == MediaStatus.ProcessingPending
                || media.Status == MediaStatus.Processing
                || media.Status == MediaStatus.DeletePending);

        var violations = new List<string>();
        Require(accounts == manifest.Accounts.Total, "seed account marker count mismatch", violations);
        Require(confirmedAccounts == manifest.Accounts.Total, "seed accounts are not login-ready", violations);
        Require(games == manifest.Catalog.GameCount, "seed game marker count mismatch", violations);
        Require(drops == 1, "seed drop marker mismatch", violations);
        Require(inventory == manifest.Drop.KeyCount, "game-key inventory count mismatch", violations);
        Require(fingerprints >= inventory, "game-key fingerprint coverage is incomplete", violations);
        Require(grantCount == distinctUsers, "a user has duplicate grants", violations);
        Require(grantCount == distinctKeys, "a key has duplicate grants", violations);
        Require(duplicateUsers == 0 && duplicateKeys == 0, "duplicate grant groups exist", violations);
        Require(grantCount <= inventory, "grants exceed inventory", violations);
        if (options.Phase == "before")
        {
            Require(grantCount == 0, "the new drop already has grants", violations);
        }
        Require(authenticationEmailPending == 0, "authentication email outbox is not empty", violations);
        Require(messagingOutboxPending == 0, "messaging outbox is not empty", violations);
        Require(notificationUnresolved == 0, "notification deliveries are unresolved", violations);
        Require(mediaInFlight == 0, "media work is still in flight", violations);

        var audit = new LoadRunAudit(
            1,
            manifest.RunId,
            options.Phase,
            DateTimeOffset.UtcNow,
            accounts,
            confirmedAccounts,
            games,
            drops,
            inventory,
            fingerprints,
            grantCount,
            distinctUsers,
            distinctKeys,
            duplicateUsers,
            duplicateKeys,
            authenticationEmailPending,
            messagingOutboxPending,
            notificationUnresolved,
            mediaInFlight,
            violations.Count == 0,
            violations);
        await JsonFile.WriteNewAsync(options.OutputPath, audit);

        Console.WriteLine($"Load-test {options.Phase} audit for {manifest.RunId}: {(audit.Passed ? "OK" : "FAILED")}");
        Console.WriteLine($"Audit: {options.OutputPath}");
        foreach (var violation in violations)
        {
            Console.Error.WriteLine($"- {violation}");
        }
        return audit.Passed ? 0 : 2;
    }

    private static async Task<long> ScalarAsync(AppDbContext db, string sql) =>
        await db.Database.SqlQueryRaw<long>(sql).SingleAsync();

    private static void Require(bool condition, string message, ICollection<string> violations)
    {
        if (!condition)
        {
            violations.Add(message);
        }
    }
}
