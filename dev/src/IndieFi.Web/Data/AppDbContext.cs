using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Credits.Domain;
using IndieFi.Web.Modules.Accounts.Domain;
using IndieFi.Web.Modules.Games.Domain;
using IndieFi.Web.Modules.Notifications.Domain;
using MassTransit;
using MassTransit.EntityFrameworkCoreIntegration;
using IndieFi.Web.Modules.Profiles.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;

namespace IndieFi.Web.Data;

/// <summary>
/// Entry point for all database access.
/// </summary>
public class AppDbContext : DbContext, IDataProtectionKeyContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<DataProtectionKey> DataProtectionKeys { get; set; } = null!;
    public DbSet<UserEmailLookup> UserEmailLookups => Set<UserEmailLookup>();
    public DbSet<EmailConfirmationToken> EmailConfirmationTokens => Set<EmailConfirmationToken>();
    public DbSet<AuthenticationEmailOutboxMessage> AuthenticationEmailOutboxMessages =>
        Set<AuthenticationEmailOutboxMessage>();
    public DbSet<EmailConfirmationResendAttempt> EmailConfirmationResendAttempts =>
        Set<EmailConfirmationResendAttempt>();
    public DbSet<PasswordResetRequestAttempt> PasswordResetRequestAttempts =>
        Set<PasswordResetRequestAttempt>();
    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();
    public DbSet<ExternalLogin> ExternalLogins => Set<ExternalLogin>();

    public DbSet<UserProfile> UserProfiles => Set<UserProfile>();
    public DbSet<UserFollow> UserFollows => Set<UserFollow>();
    public DbSet<FavoriteGame> FavoriteGames => Set<FavoriteGame>();

    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<NotificationDelivery> NotificationDeliveries =>
        Set<NotificationDelivery>();
    public DbSet<NotificationPreference> NotificationPreferences =>
        Set<NotificationPreference>();
    public DbSet<KeyDropSubscription> KeyDropSubscriptions =>
        Set<KeyDropSubscription>();

    public DbSet<Game> Games => Set<Game>();
    public DbSet<GamePlayLink> GamePlayLinks => Set<GamePlayLink>();

    public DbSet<KeyDrop> KeyDrops => Set<KeyDrop>();

    public DbSet<GameKey> GameKeys => Set<GameKey>();

    public DbSet<StoredGameKeyFingerprint> GameKeyFingerprints =>
        Set<StoredGameKeyFingerprint>();

    public DbSet<KeyGrant> KeyGrants => Set<KeyGrant>();

    public DbSet<CreditLedgerEntry> CreditLedgerEntries => Set<CreditLedgerEntry>();

    public DbSet<ReviewRequest> ReviewRequests => Set<ReviewRequest>();

    public DbSet<ReviewAssignment> ReviewAssignments => Set<ReviewAssignment>();

    public DbSet<FeedbackSubmission> FeedbackSubmissions => Set<FeedbackSubmission>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Names are mapped explicitly to snake_case: PostgreSQL folds unquoted
        // identifiers to lower case, and this project writes raw SQL by hand.
        modelBuilder.Entity<DataProtectionKey>(key =>
        {
            key.ToTable("data_protection_keys");
            key.Property(item => item.Id).HasColumnName("id");
            key.Property(item => item.FriendlyName).HasColumnName("friendly_name");
            key.Property(item => item.Xml).HasColumnName("xml");
        });

        modelBuilder.Entity<User>(user =>
        {
            user.ToTable("users", table =>
            {
                // The database must reject a negative balance regardless of application
                // mistakes, so this constraint is the final line of defense.
                table.HasCheckConstraint(
                    "CK_users_credit_balance_not_negative",
                    "credit_balance >= 0");
                table.HasCheckConstraint(
                    "CK_users_session_version_positive",
                    "session_version >= 1");
                table.HasCheckConstraint(
                    "CK_users_email_protection_complete",
                    "octet_length(email_nonce) = 12 AND " +
                    "octet_length(email_authentication_tag) = 16");
                table.HasCheckConstraint(
                    "CK_users_suspension_shape",
                    "(account_status = 'TemporarilySuspended' AND suspended_until IS NOT NULL) OR " +
                    "(account_status IN ('Active', 'IndefinitelySuspended') AND suspended_until IS NULL)");
            });

            user.HasKey(u => u.Id);
            user.Property(u => u.Id).HasColumnName("id");

            user.Property(u => u.EmailCiphertext)
                .HasColumnName("email_ciphertext")
                .IsRequired();
            user.Property(u => u.EmailNonce)
                .HasColumnName("email_nonce")
                .IsRequired();
            user.Property(u => u.EmailAuthenticationTag)
                .HasColumnName("email_authentication_tag")
                .IsRequired();
            user.Property(u => u.EmailEncryptionKeyId)
                .HasColumnName("email_encryption_key_id")
                .HasMaxLength(100)
                .IsRequired();

            user.Property(u => u.PasswordHash)
                .HasColumnName("password_hash")
                .IsRequired(false);

            user.Property(u => u.EmailConfirmedAt).HasColumnName("email_confirmed_at");
            user.Property(u => u.AccountStatus)
                .HasColumnName("account_status")
                .HasConversion<string>()
                .HasMaxLength(32)
                .HasDefaultValue(AccountStatus.Active);
            user.Property(u => u.SuspendedUntil).HasColumnName("suspended_until");
            user.Property(u => u.SessionVersion)
                .HasColumnName("session_version")
                .HasDefaultValue(1L);

            user.Property(u => u.CreditBalance)
                .HasColumnName("credit_balance")
                .IsRequired()
                .HasDefaultValue(0);

            user.Property(u => u.CreatedAt)
                .HasColumnName("created_at")
                .HasDefaultValueSql("now()")
                .ValueGeneratedOnAdd();
        });

        modelBuilder.Entity<UserEmailLookup>(lookup =>
        {
            lookup.ToTable("user_email_lookups", table =>
            {
                table.HasCheckConstraint(
                    "CK_user_email_lookups_value_length",
                    "octet_length(value) = 32");
            });
            lookup.HasKey(item => new { item.UserId, item.KeyId });
            lookup.Property(item => item.UserId).HasColumnName("user_id");
            lookup.Property(item => item.KeyId).HasColumnName("key_id").HasMaxLength(100);
            lookup.Property(item => item.Value).HasColumnName("value");
            lookup.HasIndex(item => new { item.KeyId, item.Value }).IsUnique();
            lookup.HasOne(item => item.User).WithMany().HasForeignKey(item => item.UserId);
        });

        modelBuilder.Entity<EmailConfirmationToken>(token =>
        {
            token.ToTable("email_confirmation_tokens", table =>
                table.HasCheckConstraint(
                    "CK_email_confirmation_tokens_hash_length",
                    "octet_length(token_hash) = 32"));
            token.HasKey(item => item.Id);
            token.Property(item => item.Id).HasColumnName("id");
            token.Property(item => item.UserId).HasColumnName("user_id");
            token.Property(item => item.TokenHash).HasColumnName("token_hash");
            token.Property(item => item.CreatedAt).HasColumnName("created_at");
            token.Property(item => item.ExpiresAt).HasColumnName("expires_at");
            token.Property(item => item.UsedAt).HasColumnName("used_at");
            token.Property(item => item.RevokedAt).HasColumnName("revoked_at");
            token.HasIndex(item => item.TokenHash).IsUnique();
            token.HasIndex(item => item.UserId)
                .IsUnique()
                .HasFilter("used_at IS NULL AND revoked_at IS NULL")
                .HasDatabaseName("IX_email_confirmation_tokens_user_id_unresolved");
            token.HasOne<User>().WithMany().HasForeignKey(item => item.UserId);
        });

        modelBuilder.Entity<AuthenticationEmailOutboxMessage>(message =>
        {
            message.ToTable("authentication_email_outbox_messages", table =>
            {
                table.HasCheckConstraint(
                    "CK_authentication_email_outbox_messages_payload_shape",
                    "octet_length(payload_ciphertext) > 0 AND " +
                    "octet_length(payload_nonce) = 12 AND " +
                    "octet_length(payload_authentication_tag) = 16");
                table.HasCheckConstraint(
                    "CK_authentication_email_outbox_messages_lifetime",
                    "expires_at > created_at");
                table.HasCheckConstraint(
                    "CK_authentication_email_outbox_messages_kind",
                    "kind IN ('EmailConfirmation', 'PasswordReset')");
            });
            message.HasKey(item => item.Id);
            message.Property(item => item.Id).HasColumnName("id");
            message.Property(item => item.UserId).HasColumnName("user_id");
            message.Property(item => item.Kind)
                .HasColumnName("kind")
                .HasConversion<string>()
                .HasMaxLength(32);
            message.Property(item => item.PayloadCiphertext)
                .HasColumnName("payload_ciphertext")
                .IsRequired();
            message.Property(item => item.PayloadNonce)
                .HasColumnName("payload_nonce")
                .IsRequired();
            message.Property(item => item.PayloadAuthenticationTag)
                .HasColumnName("payload_authentication_tag")
                .IsRequired();
            message.Property(item => item.PayloadKeyId)
                .HasColumnName("payload_key_id")
                .HasMaxLength(100)
                .IsRequired();
            message.Property(item => item.CreatedAt).HasColumnName("created_at");
            message.Property(item => item.ExpiresAt).HasColumnName("expires_at");
            message.Property(item => item.NextAttemptAt)
                .HasColumnName("next_attempt_at")
                .HasDefaultValueSql("now()");
            message.HasIndex(item => new { item.UserId, item.Kind }).IsUnique();
            message.HasIndex(item => item.ExpiresAt);
            message.HasOne<User>().WithMany().HasForeignKey(item => item.UserId);
        });

        modelBuilder.Entity<EmailConfirmationResendAttempt>(attempt =>
        {
            attempt.ToTable("email_confirmation_resend_attempts", table =>
                table.HasCheckConstraint(
                    "CK_email_confirmation_resend_attempts_ip_hash_length",
                    "octet_length(request_ip_hash) = 32"));
            attempt.HasKey(item => item.Id);
            attempt.Property(item => item.Id).HasColumnName("id");
            attempt.Property(item => item.UserId).HasColumnName("user_id");
            attempt.Property(item => item.RequestIpHash)
                .HasColumnName("request_ip_hash")
                .IsRequired();
            attempt.Property(item => item.RequestIpHashKeyId)
                .HasColumnName("request_ip_hash_key_id")
                .HasMaxLength(100)
                .IsRequired();
            attempt.Property(item => item.CreatedAt).HasColumnName("created_at");
            attempt.HasIndex(item => item.CreatedAt);
            attempt.HasIndex(item => new
            {
                item.RequestIpHashKeyId,
                item.RequestIpHash,
                item.CreatedAt,
            }).HasDatabaseName("IX_email_confirmation_resend_attempts_ip_created_at");
            attempt.HasIndex(item => new { item.UserId, item.CreatedAt });
            attempt.HasOne<User>()
                .WithMany()
                .HasForeignKey(item => item.UserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<PasswordResetToken>(token =>
        {
            token.ToTable("password_reset_tokens", table =>
                table.HasCheckConstraint(
                    "CK_password_reset_tokens_hash_length",
                    "octet_length(token_hash) = 32"));
            token.HasKey(item => item.Id);
            token.Property(item => item.Id).HasColumnName("id");
            token.Property(item => item.UserId).HasColumnName("user_id");
            token.Property(item => item.TokenHash).HasColumnName("token_hash");
            token.Property(item => item.CreatedAt).HasColumnName("created_at");
            token.Property(item => item.ExpiresAt).HasColumnName("expires_at");
            token.Property(item => item.UsedAt).HasColumnName("used_at");
            token.Property(item => item.RevokedAt).HasColumnName("revoked_at");
            token.HasIndex(item => item.TokenHash).IsUnique();
            token.HasIndex(item => item.UserId)
                .IsUnique()
                .HasFilter("used_at IS NULL AND revoked_at IS NULL")
                .HasDatabaseName("IX_password_reset_tokens_user_id_unresolved");
            token.HasOne<User>().WithMany().HasForeignKey(item => item.UserId);
        });

        modelBuilder.Entity<PasswordResetRequestAttempt>(attempt =>
        {
            attempt.ToTable("password_reset_request_attempts", table =>
                table.HasCheckConstraint(
                    "CK_password_reset_request_attempts_ip_hash_length",
                    "octet_length(request_ip_hash) = 32"));
            attempt.HasKey(item => item.Id);
            attempt.Property(item => item.Id).HasColumnName("id");
            attempt.Property(item => item.UserId).HasColumnName("user_id");
            attempt.Property(item => item.RequestIpHash)
                .HasColumnName("request_ip_hash")
                .IsRequired();
            attempt.Property(item => item.RequestIpHashKeyId)
                .HasColumnName("request_ip_hash_key_id")
                .HasMaxLength(100)
                .IsRequired();
            attempt.Property(item => item.CreatedAt).HasColumnName("created_at");
            attempt.HasIndex(item => item.CreatedAt);
            attempt.HasIndex(item => new
            {
                item.RequestIpHashKeyId,
                item.RequestIpHash,
                item.CreatedAt,
            }).HasDatabaseName("IX_password_reset_request_attempts_ip_created_at");
            attempt.HasIndex(item => new { item.UserId, item.CreatedAt });
            attempt.HasOne<User>()
                .WithMany()
                .HasForeignKey(item => item.UserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ExternalLogin>(login =>
        {
            login.ToTable("external_logins", table =>
            {
                table.HasCheckConstraint(
                    "CK_external_logins_supported_provider",
                    "provider = 'Google'");
                table.HasCheckConstraint(
                    "CK_external_logins_provider_user_id_not_blank",
                    "length(btrim(provider_user_id)) > 0");
            });
            login.HasKey(item => item.Id);
            login.Property(item => item.Id).HasColumnName("id");
            login.Property(item => item.UserId).HasColumnName("user_id");
            login.Property(item => item.Provider)
                .HasColumnName("provider")
                .HasMaxLength(ExternalLoginPolicy.ProviderMaxLength);
            login.Property(item => item.ProviderUserId)
                .HasColumnName("provider_user_id")
                .HasMaxLength(ExternalLoginPolicy.ProviderUserIdMaxLength);
            login.Property(item => item.LinkedAt).HasColumnName("linked_at").HasDefaultValueSql("now()");
            login.HasIndex(item => new { item.Provider, item.ProviderUserId }).IsUnique();
            login.HasIndex(item => new { item.UserId, item.Provider }).IsUnique();
            login.HasOne<User>().WithMany().HasForeignKey(item => item.UserId);
        });

        modelBuilder.Entity<UserProfile>(profile =>
        {
            profile.ToTable("user_profiles", table =>
            {
                table.HasCheckConstraint(
                    "CK_user_profiles_nickname_shape",
                    "(nickname IS NULL AND normalized_nickname IS NULL) OR " +
                    "(nickname IS NOT NULL AND normalized_nickname IS NOT NULL AND " +
                    $"char_length(nickname) BETWEEN {UserProfilePolicy.NicknameMinLength} " +
                    $"AND {UserProfilePolicy.NicknameMaxLength} AND " +
                    $"char_length(normalized_nickname) BETWEEN {UserProfilePolicy.NicknameMinLength} " +
                    $"AND {UserProfilePolicy.NicknameMaxLength})");
                table.HasCheckConstraint(
                    "CK_user_profiles_biography_length",
                    $"char_length(biography) <= {UserProfilePolicy.BiographyMaxLength}");
                table.HasCheckConstraint(
                    "CK_user_profiles_timestamps",
                    "updated_at >= created_at AND " +
                    "(nickname_changed_at IS NULL OR nickname_changed_at >= created_at)");
            });

            profile.HasKey(item => item.UserId);
            profile.Property(item => item.UserId).HasColumnName("user_id");
            profile.Property(item => item.Nickname)
                .HasColumnName("nickname")
                .HasMaxLength(UserProfilePolicy.NicknameMaxLength);
            profile.Property(item => item.NormalizedNickname)
                .HasColumnName("normalized_nickname")
                .HasMaxLength(UserProfilePolicy.NicknameMaxLength);
            profile.Property(item => item.Biography)
                .HasColumnName("biography")
                .HasMaxLength(UserProfilePolicy.BiographyMaxLength)
                .HasDefaultValue(string.Empty);
            profile.Property(item => item.NicknameChangedAt)
                .HasColumnName("nickname_changed_at");
            profile.Property(item => item.CreatedAt)
                .HasColumnName("created_at")
                .HasDefaultValueSql("now()")
                .ValueGeneratedOnAdd();
            profile.Property(item => item.UpdatedAt)
                .HasColumnName("updated_at")
                .HasDefaultValueSql("now()");
            profile.HasIndex(item => item.NormalizedNickname).IsUnique();
            profile.HasOne<User>()
                .WithOne()
                .HasForeignKey<UserProfile>(item => item.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserFollow>(follow =>
        {
            follow.ToTable("user_follows", table =>
            {
                table.HasCheckConstraint(
                    "CK_user_follows_not_self",
                    "follower_id <> followed_user_id");
            });

            follow.HasKey(item => new { item.FollowerId, item.FollowedUserId });
            follow.Property(item => item.FollowerId)
                .HasColumnName("follower_id");
            follow.Property(item => item.FollowedUserId)
                .HasColumnName("followed_user_id");
            follow.Property(item => item.CreatedAt)
                .HasColumnName("created_at")
                .HasDefaultValueSql("now()")
                .ValueGeneratedOnAdd();
            follow.HasIndex(item => item.FollowedUserId);
            follow.HasOne<User>()
                .WithMany()
                .HasForeignKey(item => item.FollowerId)
                .OnDelete(DeleteBehavior.Cascade);
            follow.HasOne<User>()
                .WithMany()
                .HasForeignKey(item => item.FollowedUserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<FavoriteGame>(favorite =>
        {
            favorite.ToTable("favorite_games");
            favorite.HasKey(item => new { item.UserId, item.GameId });
            favorite.Property(item => item.UserId)
                .HasColumnName("user_id");
            favorite.Property(item => item.GameId)
                .HasColumnName("game_id");
            favorite.Property(item => item.CreatedAt)
                .HasColumnName("created_at")
                .HasDefaultValueSql("now()")
                .ValueGeneratedOnAdd();
            favorite.HasIndex(item => item.GameId);
            favorite.HasOne<User>()
                .WithMany()
                .HasForeignKey(item => item.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            favorite.HasOne<Game>()
                .WithMany()
                .HasForeignKey(item => item.GameId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Game>(game =>
        {
            game.ToTable("games");

            game.HasKey(g => g.Id);
            game.Property(g => g.Id).HasColumnName("id");

            game.Property(g => g.OwnerId)
                .HasColumnName("owner_id")
                .IsRequired();

            game.Property(g => g.Title)
                .HasColumnName("title")
                .HasMaxLength(200)
                .IsRequired();

            game.Property(g => g.Description)
                .HasColumnName("description");

            game.Property(g => g.CreatedAt)
                .HasColumnName("created_at")
                .HasDefaultValueSql("now()")
                .ValueGeneratedOnAdd();

            // A game belongs to the user who registered it; the foreign key lets
            // the database reject a game with no real owner.
            game.HasOne<User>()
                .WithMany()
                .HasForeignKey(g => g.OwnerId);
        });

        modelBuilder.Entity<GamePlayLink>(link =>
        {
            link.ToTable("game_play_links", table =>
            {
                table.HasCheckConstraint(
                    "CK_game_play_links_display_name_not_blank",
                    "length(btrim(display_name)) > 0");
                table.HasCheckConstraint(
                    "CK_game_play_links_provider",
                    "provider IN ('Steam', 'ItchIo', 'DeveloperWebsite', 'Other')");
                table.HasCheckConstraint(
                    "CK_game_play_links_target_platform",
                    "target_platform IS NULL OR target_platform IN " +
                    "('Windows', 'MacOS', 'Linux', 'Web', 'Android', 'Ios', 'Multiple', 'Other')");
                table.HasCheckConstraint(
                    "CK_game_play_links_url_not_blank",
                    "length(btrim(url)) > 0");
                table.HasCheckConstraint(
                    "CK_game_play_links_sort_order",
                    $"sort_order >= 0 AND sort_order < {GamePlayLinkPolicy.MaxLinksPerGame}");
            });

            link.HasKey(item => item.Id);
            link.Property(item => item.Id).HasColumnName("id");
            link.Property(item => item.GameId).HasColumnName("game_id").IsRequired();
            link.Property(item => item.DisplayName)
                .HasColumnName("display_name")
                .HasMaxLength(GamePlayLinkPolicy.DisplayNameMaxLength)
                .IsRequired();
            link.Property(item => item.Provider)
                .HasColumnName("provider")
                .HasConversion<string>()
                .HasMaxLength(32)
                .IsRequired();
            link.Property(item => item.TargetPlatform)
                .HasColumnName("target_platform")
                .HasConversion<string>()
                .HasMaxLength(32);
            link.Property(item => item.Url)
                .HasColumnName("url")
                .HasMaxLength(GamePlayLinkPolicy.UrlMaxLength)
                .IsRequired();
            link.Property(item => item.SortOrder)
                .HasColumnName("sort_order")
                .IsRequired();

            link.HasIndex(item => new { item.GameId, item.SortOrder }).IsUnique();
            link.HasOne<Game>()
                .WithMany()
                .HasForeignKey(item => item.GameId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<KeyDrop>(keyDrop =>
        {
            keyDrop.ToTable("key_drops", table =>
            {
                table.HasCheckConstraint(
                    "CK_key_drops_total_keys_nonnegative",
                    "total_keys >= 0");
                table.HasCheckConstraint(
                    "CK_key_drops_state_version_nonnegative",
                    "state_version >= 0");
                table.HasCheckConstraint(
                    "CK_key_drops_minimum_account_age_days_range",
                    "minimum_account_age_days IS NULL OR "
                    + "minimum_account_age_days BETWEEN 1 AND 365");
                table.HasCheckConstraint(
                    "CK_key_drops_minimum_completed_review_count_range",
                    "minimum_completed_review_count IS NULL OR "
                    + "minimum_completed_review_count BETWEEN 1 AND 100");
            });

            keyDrop.HasKey(d => d.Id);
            keyDrop.Property(d => d.Id).HasColumnName("id");

            keyDrop.Property(d => d.GameId)
                .HasColumnName("game_id")
                .IsRequired();

            keyDrop.Property(d => d.OwnerId)
                .HasColumnName("owner_id")
                .IsRequired();

            keyDrop.Property(d => d.OpensAt)
                .HasColumnName("opens_at")
                .IsRequired();

            keyDrop.Property(d => d.TotalKeys)
                .HasColumnName("total_keys")
                .IsRequired();

            keyDrop.Property(d => d.MinimumAccountAgeDays)
                .HasColumnName("minimum_account_age_days");

            keyDrop.Property(d => d.MinimumCompletedReviewCount)
                .HasColumnName("minimum_completed_review_count");

            keyDrop.Property(d => d.ClosedAt)
                .HasColumnName("closed_at");

            keyDrop.Property(d => d.StateVersion)
                .HasColumnName("state_version")
                .HasDefaultValue(0L)
                .IsConcurrencyToken()
                .IsRequired();

            keyDrop.Property(d => d.CreatedAt)
                .HasColumnName("created_at")
                .HasDefaultValueSql("now()")
                .ValueGeneratedOnAdd();

            keyDrop.HasOne<Game>()
                .WithMany()
                .HasForeignKey(d => d.GameId);

            keyDrop.HasOne<User>()
                .WithMany()
                .HasForeignKey(d => d.OwnerId);
        });

        modelBuilder.Entity<GameKey>(gameKey =>
        {
            gameKey.ToTable("game_keys", table =>
            {
                table.HasCheckConstraint(
                    "CK_game_keys_encryption_shape",
                    "octet_length(ciphertext) BETWEEN 1 AND 800 AND "
                    + "octet_length(nonce) = 12 AND "
                    + "octet_length(authentication_tag) = 16 AND "
                    + "char_length(encryption_key_id) BETWEEN 1 AND 100 AND "
                    + "encryption_key_id = btrim(encryption_key_id)");
            });

            gameKey.HasKey(key => key.Id);
            gameKey.Property(key => key.Id).HasColumnName("id");

            gameKey.Property(key => key.DropId)
                .HasColumnName("drop_id")
                .IsRequired();

            gameKey.Property(key => key.Ciphertext)
                .HasColumnName("ciphertext")
                .IsRequired();

            gameKey.Property(key => key.Nonce)
                .HasColumnName("nonce")
                .IsRequired();

            gameKey.Property(key => key.AuthenticationTag)
                .HasColumnName("authentication_tag")
                .IsRequired();

            gameKey.Property(key => key.EncryptionKeyId)
                .HasColumnName("encryption_key_id")
                .HasMaxLength(100)
                .IsRequired();

            gameKey.Property(key => key.CreatedAt)
                .HasColumnName("created_at")
                .HasDefaultValueSql("now()")
                .ValueGeneratedOnAdd();

            gameKey.HasAlternateKey(key => new { key.Id, key.DropId });

            gameKey.HasOne(key => key.Drop)
                .WithMany()
                .HasForeignKey(key => key.DropId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<StoredGameKeyFingerprint>(fingerprint =>
        {
            fingerprint.ToTable("game_key_fingerprints", table =>
            {
                table.HasCheckConstraint(
                    "CK_game_key_fingerprints_key_id_shape",
                    "char_length(key_id) BETWEEN 1 AND 100 AND key_id = btrim(key_id)");
                table.HasCheckConstraint(
                    "CK_game_key_fingerprints_value_length",
                    "octet_length(value) = 32");
            });

            fingerprint.HasKey(item => new { item.GameKeyId, item.KeyId });
            fingerprint.Property(item => item.GameKeyId)
                .HasColumnName("game_key_id");
            fingerprint.Property(item => item.KeyId)
                .HasColumnName("key_id")
                .HasMaxLength(100);
            fingerprint.Property(item => item.Value)
                .HasColumnName("value");

            fingerprint.HasIndex(item => new { item.KeyId, item.Value })
                .IsUnique();
            fingerprint.HasOne(item => item.GameKey)
                .WithMany()
                .HasForeignKey(item => item.GameKeyId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<KeyGrant>(keyGrant =>
        {
            keyGrant.ToTable("key_grants");

            keyGrant.HasKey(g => g.Id);
            keyGrant.Property(g => g.Id).HasColumnName("id");

            keyGrant.Property(g => g.DropId)
                .HasColumnName("drop_id")
                .IsRequired();

            keyGrant.Property(g => g.UserId)
                .HasColumnName("user_id")
                .IsRequired();

            keyGrant.Property(g => g.KeyId)
                .HasColumnName("key_id")
                .IsRequired();

            keyGrant.Property(g => g.CreatedAt)
                .HasColumnName("created_at")
                .HasDefaultValueSql("now()")
                .ValueGeneratedOnAdd();

            keyGrant.HasOne<KeyDrop>()
                .WithMany()
                .HasForeignKey(g => g.DropId)
                .OnDelete(DeleteBehavior.Cascade);

            keyGrant.HasOne<User>()
                .WithMany()
                .HasForeignKey(g => g.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            keyGrant.HasOne<GameKey>()
                .WithMany()
                .HasForeignKey(grant => new { grant.KeyId, grant.DropId })
                .HasPrincipalKey(key => new { key.Id, key.DropId })
                .OnDelete(DeleteBehavior.Cascade);

            // A user can receive only one key from a drop.
            keyGrant.HasIndex(g => new { g.DropId, g.UserId }).IsUnique();

            // A game key can be granted to only one user.
            keyGrant.HasIndex(g => g.KeyId).IsUnique();
        });

        modelBuilder.Entity<Notification>(notification =>
        {
            notification.ToTable("notifications", table =>
            {
                table.HasCheckConstraint(
                    "CK_notifications_event_id",
                    "event_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                table.HasCheckConstraint(
                    "CK_notifications_source",
                    "source_id > 0 AND length(btrim(source_type)) > 0");
                table.HasCheckConstraint(
                    "CK_notifications_lifetime",
                    "occurred_at <= created_at AND expires_at > created_at");
                table.HasCheckConstraint(
                    "CK_notifications_destination_path",
                    "destination_path IS NULL OR "
                    + "(destination_path LIKE '/%' AND destination_path NOT LIKE '//%')");
                table.HasCheckConstraint(
                    "CK_notifications_content_shape",
                    "(content_removed_at IS NULL AND title IS NOT NULL AND body IS NOT NULL "
                    + "AND length(btrim(title)) > 0 AND length(btrim(body)) > 0) OR "
                    + "(content_removed_at IS NOT NULL AND content_removed_at >= created_at "
                    + "AND title IS NULL AND body IS NULL AND destination_path IS NULL "
                    + "AND is_web_visible = FALSE)");
                table.HasCheckConstraint(
                    "CK_notifications_read_shape",
                    "read_at IS NULL OR "
                    + "(is_web_visible = TRUE AND read_at >= created_at)");
                table.HasCheckConstraint(
                    "CK_notifications_kind",
                    "kind IN ('ReviewAssigned', 'ReviewDeadlineSoon', 'ReviewExpired', "
                    + "'FeedbackCompleted', 'KeyDropOpened', "
                    + "'FavoriteGameKeyDropCreated', 'FollowedUserGamePublished', "
                    + "'ReportResolved', 'AccountStatusChanged')");
            });

            notification.HasKey(item => item.Id);
            notification.Property(item => item.Id).HasColumnName("id");
            notification.Property(item => item.UserId).HasColumnName("user_id");
            notification.Property(item => item.EventId).HasColumnName("event_id");
            notification.Property(item => item.Kind)
                .HasColumnName("kind")
                .HasConversion<string>()
                .HasMaxLength(40);
            notification.Property(item => item.SourceType)
                .HasColumnName("source_type")
                .HasMaxLength(NotificationPolicy.SourceTypeMaxLength);
            notification.Property(item => item.SourceId).HasColumnName("source_id");
            notification.Property(item => item.Title)
                .HasColumnName("title")
                .HasMaxLength(NotificationPolicy.TitleMaxLength);
            notification.Property(item => item.Body)
                .HasColumnName("body")
                .HasMaxLength(NotificationPolicy.BodyMaxLength);
            notification.Property(item => item.DestinationPath)
                .HasColumnName("destination_path")
                .HasMaxLength(NotificationPolicy.DestinationPathMaxLength);
            notification.Property(item => item.IsWebVisible)
                .HasColumnName("is_web_visible");
            notification.Property(item => item.OccurredAt).HasColumnName("occurred_at");
            notification.Property(item => item.CreatedAt)
                .HasColumnName("created_at")
                .HasDefaultValueSql("now()")
                .ValueGeneratedOnAdd();
            notification.Property(item => item.ExpiresAt).HasColumnName("expires_at");
            notification.Property(item => item.ReadAt).HasColumnName("read_at");
            notification.Property(item => item.ContentRemovedAt)
                .HasColumnName("content_removed_at");
            notification.HasIndex(item => new { item.UserId, item.EventId }).IsUnique();
            notification.HasIndex(item => new { item.UserId, item.CreatedAt })
                .IsDescending(false, true)
                .HasDatabaseName("IX_notifications_user_created_at");
            notification.HasIndex(item => new
                {
                    item.UserId,
                    item.ReadAt,
                    item.CreatedAt,
                })
                .IsDescending(false, false, true)
                .HasFilter("is_web_visible = TRUE AND read_at IS NULL "
                    + "AND content_removed_at IS NULL")
                .HasDatabaseName("IX_notifications_user_unread");
            notification.HasOne<User>()
                .WithMany()
                .HasForeignKey(item => item.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<NotificationDelivery>(delivery =>
        {
            delivery.ToTable("notification_deliveries", table =>
            {
                table.HasCheckConstraint(
                    "CK_notification_deliveries_message_id",
                    "message_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                table.HasCheckConstraint(
                    "CK_notification_deliveries_attempt_count",
                    "attempt_count >= 0");
                table.HasCheckConstraint(
                    "CK_notification_deliveries_channel",
                    "channel IN ('Web', 'Email')");
                table.HasCheckConstraint(
                    "CK_notification_deliveries_status",
                    "status IN ('Suppressed', 'Pending', 'Delivered', 'Failed', 'Expired')");
                table.HasCheckConstraint(
                    "CK_notification_deliveries_timestamp_order",
                    "(next_attempt_at IS NULL OR next_attempt_at >= created_at) AND "
                    + "(last_attempt_at IS NULL OR last_attempt_at >= created_at) AND "
                    + "(delivered_at IS NULL OR delivered_at >= created_at) AND "
                    + "(failed_at IS NULL OR failed_at >= created_at)");
                table.HasCheckConstraint(
                    "CK_notification_deliveries_state_shape",
                    "(status = 'Suppressed' AND attempt_count = 0 AND next_attempt_at IS NULL "
                    + "AND delivered_at IS NULL AND failed_at IS NULL) OR "
                    + "(status = 'Pending' AND next_attempt_at IS NOT NULL "
                    + "AND delivered_at IS NULL AND failed_at IS NULL) OR "
                    + "(status = 'Delivered' AND next_attempt_at IS NULL "
                    + "AND delivered_at IS NOT NULL AND failed_at IS NULL) OR "
                    + "(status IN ('Failed', 'Expired') AND next_attempt_at IS NULL "
                    + "AND delivered_at IS NULL AND failed_at IS NOT NULL)");
            });

            delivery.HasKey(item => item.Id);
            delivery.Property(item => item.Id).HasColumnName("id");
            delivery.Property(item => item.NotificationId)
                .HasColumnName("notification_id");
            delivery.Property(item => item.MessageId).HasColumnName("message_id");
            delivery.Property(item => item.Channel)
                .HasColumnName("channel")
                .HasConversion<string>()
                .HasMaxLength(16);
            delivery.Property(item => item.Status)
                .HasColumnName("status")
                .HasConversion<string>()
                .HasMaxLength(16);
            delivery.Property(item => item.AttemptCount)
                .HasColumnName("attempt_count");
            delivery.Property(item => item.CreatedAt)
                .HasColumnName("created_at")
                .HasDefaultValueSql("now()")
                .ValueGeneratedOnAdd();
            delivery.Property(item => item.NextAttemptAt)
                .HasColumnName("next_attempt_at");
            delivery.Property(item => item.LastAttemptAt)
                .HasColumnName("last_attempt_at");
            delivery.Property(item => item.DeliveredAt).HasColumnName("delivered_at");
            delivery.Property(item => item.FailedAt).HasColumnName("failed_at");
            delivery.Property(item => item.LastErrorCode)
                .HasColumnName("last_error_code")
                .HasMaxLength(NotificationPolicy.ErrorCodeMaxLength);
            delivery.HasIndex(item => item.MessageId).IsUnique();
            delivery.HasIndex(item => new { item.NotificationId, item.Channel }).IsUnique();
            delivery.HasIndex(item => new { item.Status, item.NextAttemptAt });
            delivery.HasOne(item => item.Notification)
                .WithMany(item => item.Deliveries)
                .HasForeignKey(item => item.NotificationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<NotificationPreference>(preference =>
        {
            preference.ToTable("notification_preferences", table =>
            {
                table.HasCheckConstraint(
                    "CK_notification_preferences_kind",
                    "kind IN ('ReviewAssigned', 'ReviewDeadlineSoon', 'ReviewExpired', "
                    + "'FeedbackCompleted', 'KeyDropOpened', "
                    + "'FavoriteGameKeyDropCreated', 'FollowedUserGamePublished', "
                    + "'ReportResolved', 'AccountStatusChanged')");
                table.HasCheckConstraint(
                    "CK_notification_preferences_mandatory",
                    "kind <> 'AccountStatusChanged' OR "
                    + "(web_enabled = TRUE AND email_enabled = TRUE)");
            });

            preference.HasKey(item => new { item.UserId, item.Kind });
            preference.Property(item => item.UserId).HasColumnName("user_id");
            preference.Property(item => item.Kind)
                .HasColumnName("kind")
                .HasConversion<string>()
                .HasMaxLength(40);
            preference.Property(item => item.WebEnabled).HasColumnName("web_enabled");
            preference.Property(item => item.EmailEnabled).HasColumnName("email_enabled");
            preference.Property(item => item.UpdatedAt)
                .HasColumnName("updated_at")
                .HasDefaultValueSql("now()");
            preference.HasOne<User>()
                .WithMany()
                .HasForeignKey(item => item.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<KeyDropSubscription>(subscription =>
        {
            subscription.ToTable("key_drop_subscriptions");
            subscription.HasKey(item => new { item.UserId, item.DropId });
            subscription.Property(item => item.UserId).HasColumnName("user_id");
            subscription.Property(item => item.DropId).HasColumnName("drop_id");
            subscription.Property(item => item.CreatedAt)
                .HasColumnName("created_at")
                .HasDefaultValueSql("now()")
                .ValueGeneratedOnAdd();
            subscription.HasIndex(item => item.DropId);
            subscription.HasOne<User>()
                .WithMany()
                .HasForeignKey(item => item.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            subscription.HasOne<KeyDrop>()
                .WithMany()
                .HasForeignKey(item => item.DropId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Credit history is append-only: rows are never updated or deleted, and
        // a user's balance is the sum of all of their delta values.
        modelBuilder.Entity<CreditLedgerEntry>(creditLedgerEntry =>
        {
            creditLedgerEntry.ToTable("credit_ledger_entries", table =>
            {
                table.HasCheckConstraint(
                    "CK_credit_ledger_entries_kind",
                    "kind IN ('Grant', 'Spend', 'Earn', 'Refund')");
                table.HasCheckConstraint(
                    "CK_credit_ledger_entries_kind_delta",
                    "kind NOT IN ('Grant', 'Spend', 'Earn', 'Refund') OR "
                    + "(kind = 'Grant' AND delta > 0) OR "
                    + "(kind = 'Spend' AND delta = -1) OR "
                    + "(kind = 'Earn' AND delta = 1) OR "
                    + "(kind = 'Refund' AND delta = 1)");
            });

            creditLedgerEntry.HasKey(e => e.Id);
            creditLedgerEntry.Property(e => e.Id).HasColumnName("id");

            creditLedgerEntry.Property(e => e.UserId)
                .HasColumnName("user_id")
                .IsRequired();

            // Text is readable in psql audit records and prevents enum reordering
            // from changing the meaning of historical entries.
            creditLedgerEntry.Property(e => e.Kind)
                .HasColumnName("kind")
                .HasConversion<string>()
                .HasMaxLength(16)
                .IsRequired();

            creditLedgerEntry.Property(e => e.Delta)
                .HasColumnName("delta")
                .IsRequired();

            creditLedgerEntry.Property(e => e.CreatedAt)
                .HasColumnName("created_at")
                .HasDefaultValueSql("now()")
                .ValueGeneratedOnAdd();

            creditLedgerEntry.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId);

            // Origin rows carry their user ID as well as the ledger ID. Referencing this pair
            // makes the database reject attaching one user's movement to another user's action.
            creditLedgerEntry.HasAlternateKey(e => new { e.Id, e.UserId });

            // The ledger screen reads one user's history in this order.
            creditLedgerEntry.HasIndex(e => e.UserId);
        });

        modelBuilder.Entity<ReviewRequest>(reviewRequest =>
        {
            reviewRequest.ToTable("review_requests", table =>
            {
                table.HasCheckConstraint(
                    "CK_review_requests_status",
                    "status IN ('Queued', 'Assigned', 'Completed', 'Cancelled')");
                table.HasCheckConstraint(
                    "CK_review_requests_terminal_timestamps",
                    "(status IN ('Queued', 'Assigned') AND completed_at IS NULL AND cancelled_at IS NULL) OR "
                    + "(status = 'Completed' AND completed_at IS NOT NULL AND cancelled_at IS NULL) OR "
                    + "(status = 'Cancelled' AND cancelled_at IS NOT NULL AND completed_at IS NULL)");
                table.HasCheckConstraint(
                    "CK_review_requests_creation_operation_id",
                    "creation_operation_id <> '00000000-0000-0000-0000-000000000000'::uuid");
            });

            reviewRequest.HasKey(r => r.Id);
            reviewRequest.Property(r => r.Id).HasColumnName("id");

            reviewRequest.Property(r => r.GameId)
                .HasColumnName("game_id")
                .IsRequired();

            reviewRequest.Property(r => r.RequesterId)
                .HasColumnName("requester_id")
                .IsRequired();

            reviewRequest.Property(r => r.CreationOperationId)
                .HasColumnName("creation_operation_id")
                .IsRequired();

            reviewRequest.Property(r => r.SpendEntryId)
                .HasColumnName("spend_entry_id")
                .IsRequired();

            reviewRequest.Property(r => r.RefundEntryId)
                .HasColumnName("refund_entry_id");

            reviewRequest.Property(r => r.Status)
                .HasColumnName("status")
                .HasConversion<string>()
                .HasMaxLength(16)
                .IsRequired()
                .HasDefaultValue(ReviewRequestStatus.Queued);

            reviewRequest.Property(r => r.CompletedAt)
                .HasColumnName("completed_at");

            reviewRequest.Property(r => r.CancelledAt)
                .HasColumnName("cancelled_at");

            reviewRequest.Property(r => r.CreatedAt)
                .HasColumnName("created_at")
                .HasDefaultValueSql("now()")
                .ValueGeneratedOnAdd();

            // A composite foreign key can only point at a unique column pair, so the assignment
            // table needs this declared before it can reference the request and its game together.
            // The pair is already unique because id alone is, but SQL still requires the constraint.
            reviewRequest.HasAlternateKey(r => new { r.Id, r.GameId });

            reviewRequest.HasOne<Game>()
                .WithMany()
                .HasForeignKey(r => r.GameId)
                .OnDelete(DeleteBehavior.Cascade);

            reviewRequest.HasOne<User>()
                .WithMany()
                .HasForeignKey(r => r.RequesterId)
                .OnDelete(DeleteBehavior.Cascade);

            reviewRequest.HasOne(r => r.SpendEntry)
                .WithMany()
                .HasForeignKey(r => new { r.SpendEntryId, r.RequesterId })
                .HasPrincipalKey(e => new { e.Id, e.UserId })
                .OnDelete(DeleteBehavior.Cascade);

            reviewRequest.HasOne(r => r.RefundEntry)
                .WithMany()
                .HasForeignKey(r => new { r.RefundEntryId, r.RequesterId })
                .HasPrincipalKey(e => new { e.Id, e.UserId });

            // One credit spend creates exactly one request, so the database rejects
            // a second request even if application code tries to create it.
            reviewRequest.HasIndex(r => r.SpendEntryId).IsUnique();

            // One refund ledger row can settle only one request, so the database rejects
            // accidental reuse even if a future write path bypasses the cancellation service.
            reviewRequest.HasIndex(r => r.RefundEntryId).IsUnique();

            reviewRequest.HasIndex(r => new { r.RequesterId, r.CreationOperationId })
                .IsUnique();

            // Claiming reads the oldest waiting request. Holding only those rows keeps
            // assigned and completed history from being walked past on every claim,
            // which a filter on the primary key index could not avoid. Expressing the
            // same condition without this column is impossible: a partial index cannot
            // look at another table to ask whether an assignment exists.
            reviewRequest.HasIndex(r => r.Id)
                .HasFilter("status = 'Queued'")
                .HasDatabaseName("IX_review_requests_queued");
        });

        modelBuilder.Entity<ReviewAssignment>(reviewAssignment =>
        {
            reviewAssignment.ToTable("review_assignments", table =>
            {
                table.HasCheckConstraint(
                    "CK_review_assignments_status",
                    "status IN ('Active', 'Completed', 'Expired')");
                table.HasCheckConstraint(
                    "CK_review_assignments_terminal_timestamps",
                    "(status = 'Active' AND completed_at IS NULL AND expired_at IS NULL) OR "
                    + "(status = 'Completed' AND completed_at IS NOT NULL AND expired_at IS NULL) OR "
                    + "(status = 'Expired' AND expired_at IS NOT NULL AND completed_at IS NULL)");
                table.HasCheckConstraint(
                    "CK_review_assignments_claim_operation_id",
                    "claim_operation_id <> '00000000-0000-0000-0000-000000000000'::uuid");
            });

            reviewAssignment.HasKey(a => a.Id);
            reviewAssignment.Property(a => a.Id).HasColumnName("id");

            reviewAssignment.Property(a => a.RequestId)
                .HasColumnName("request_id")
                .IsRequired();

            reviewAssignment.Property(a => a.GameId)
                .HasColumnName("game_id")
                .IsRequired();

            reviewAssignment.Property(a => a.ReviewerId)
                .HasColumnName("reviewer_id")
                .IsRequired();

            reviewAssignment.Property(a => a.ClaimOperationId)
                .HasColumnName("claim_operation_id")
                .IsRequired();

            reviewAssignment.Property(a => a.Status)
                .HasColumnName("status")
                .HasConversion<string>()
                .HasMaxLength(16)
                .IsRequired();

            reviewAssignment.Property(a => a.LeasedUntil)
                .HasColumnName("leased_until")
                .IsRequired();

            reviewAssignment.Property(a => a.CompletedAt)
                .HasColumnName("completed_at");

            reviewAssignment.Property(a => a.ExpiredAt)
                .HasColumnName("expired_at");

            reviewAssignment.Property(a => a.DeadlineNotificationStagedAt)
                .HasColumnName("deadline_notification_staged_at");

            reviewAssignment.Property(a => a.CreatedAt)
                .HasColumnName("created_at")
                .HasDefaultValueSql("now()")
                .ValueGeneratedOnAdd();

            // The game number now lives in two tables, so the database has to guarantee the copies
            // cannot drift. Pointing the foreign key at the (id, game_id) pair means an assignment
            // whose game does not match its own request is rejected, not merely unlikely.
            reviewAssignment.HasOne<ReviewRequest>()
                .WithMany()
                .HasForeignKey(a => new { a.RequestId, a.GameId })
                .HasPrincipalKey(r => new { r.Id, r.GameId })
                .OnDelete(DeleteBehavior.Cascade);

            reviewAssignment.HasOne<User>()
                .WithMany()
                .HasForeignKey(a => a.ReviewerId)
                .OnDelete(DeleteBehavior.Cascade);

            reviewAssignment.HasAlternateKey(a => new { a.Id, a.ReviewerId });

            // One request may have at most one assignment that is still alive, so a second
            // reviewer cannot be handed work that someone else already holds. Expired
            // assignments are excluded because expiry returns the request to the queue.
            reviewAssignment.HasIndex(a => a.RequestId)
                .IsUnique()
                .HasFilter("status <> 'Expired'");

            // A reviewer may hold at most one assignment per game, so the same game cannot be
            // reviewed twice for credit by one person. Expired assignments are excluded so someone
            // who ran out of time is not shut out of that game forever, matching the filter the
            // request-level unique index already uses.
            reviewAssignment.HasIndex(a => new { a.ReviewerId, a.GameId })
                .IsUnique()
                .HasFilter("status <> 'Expired'");

            reviewAssignment.HasIndex(a => new { a.ReviewerId, a.ClaimOperationId })
                .IsUnique();

            reviewAssignment.HasIndex(a => new { a.Status, a.LeasedUntil })
                .HasFilter("deadline_notification_staged_at IS NULL");
        });

        modelBuilder.Entity<FeedbackSubmission>(feedbackSubmission =>
        {
            feedbackSubmission.ToTable("feedback_submissions", table =>
            {
                // Credit is earned per submission, so an empty-ish submission would buy a credit.
                // The database refuses it regardless of which code path inserts the row.
                table.HasCheckConstraint(
                    "CK_feedback_submissions_body_long_enough",
                    "length(btrim(body)) >= 30");
            });

            feedbackSubmission.HasKey(s => s.Id);
            feedbackSubmission.Property(s => s.Id).HasColumnName("id");

            feedbackSubmission.Property(s => s.AssignmentId)
                .HasColumnName("assignment_id")
                .IsRequired();

            feedbackSubmission.Property(s => s.ReviewerId)
                .HasColumnName("reviewer_id")
                .IsRequired();

            feedbackSubmission.Property(s => s.EarnEntryId)
                .HasColumnName("earn_entry_id")
                .IsRequired();

            feedbackSubmission.Property(s => s.Body)
                .HasColumnName("body")
                .HasMaxLength(4000)
                .IsRequired();

            feedbackSubmission.Property(s => s.CreatedAt)
                .HasColumnName("created_at")
                .HasDefaultValueSql("now()")
                .ValueGeneratedOnAdd();

            feedbackSubmission.HasOne<ReviewAssignment>()
                .WithMany()
                .HasForeignKey(s => new { s.AssignmentId, s.ReviewerId })
                .HasPrincipalKey(a => new { a.Id, a.ReviewerId })
                .OnDelete(DeleteBehavior.Cascade);

            feedbackSubmission.HasOne(s => s.EarnEntry)
                .WithMany()
                .HasForeignKey(s => new { s.EarnEntryId, s.ReviewerId })
                .HasPrincipalKey(e => new { e.Id, e.UserId })
                .OnDelete(DeleteBehavior.Restrict);

            // One assignment produces at most one submission, so a second submit cannot create a
            // second row even if the conditional update above is somehow bypassed.
            feedbackSubmission.HasIndex(s => s.AssignmentId).IsUnique();

            // A ledger movement can pay for only one feedback submission.
            feedbackSubmission.HasIndex(s => s.EarnEntryId).IsUnique();
        });

        modelBuilder.AddInboxStateEntity(entity =>
            entity.ToTable("message_inbox_states"));
        modelBuilder.AddOutboxMessageEntity(entity =>
            entity.ToTable("message_outbox_messages"));
        modelBuilder.AddOutboxStateEntity(entity =>
            entity.ToTable("message_outbox_states"));

        MapMassTransitColumnsToSnakeCase(modelBuilder);
    }

    private static void MapMassTransitColumnsToSnakeCase(ModelBuilder modelBuilder)
    {
        Type[] outboxEntityTypes =
        [
            typeof(InboxState),
            typeof(OutboxMessage),
            typeof(OutboxState),
        ];

        foreach (var entityType in outboxEntityTypes)
        {
            var metadata = modelBuilder.Model.FindEntityType(entityType)
                ?? throw new InvalidOperationException(
                    $"MassTransit outbox entity '{entityType.Name}' is not configured.");

            foreach (var property in metadata.GetProperties())
            {
                property.SetColumnName(ToSnakeCase(property.Name));
            }
        }
    }

    private static string ToSnakeCase(string value)
    {
        var buffer = new System.Text.StringBuilder(value.Length + 8);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsUpper(character) && index > 0)
            {
                buffer.Append('_');
            }

            buffer.Append(char.ToLowerInvariant(character));
        }

        return buffer.ToString();
    }
}
