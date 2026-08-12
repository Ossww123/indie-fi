using IndieFi.Web.Entities;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Data;

/// <summary>
/// Entry point for all database access.
/// </summary>
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public DbSet<User> Users => Set<User>();

    public DbSet<Game> Games => Set<Game>();

    public DbSet<KeyDrop> KeyDrops => Set<KeyDrop>();

    public DbSet<SteamKey> SteamKeys => Set<SteamKey>();

    public DbSet<KeyGrant> KeyGrants => Set<KeyGrant>();

    public DbSet<CreditLedgerEntry> CreditLedgerEntries => Set<CreditLedgerEntry>();

    public DbSet<ReviewRequest> ReviewRequests => Set<ReviewRequest>();

    public DbSet<ReviewAssignment> ReviewAssignments => Set<ReviewAssignment>();

    public DbSet<FeedbackSubmission> FeedbackSubmissions => Set<FeedbackSubmission>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Names are mapped explicitly to snake_case: PostgreSQL folds unquoted
        // identifiers to lower case, and this project writes raw SQL by hand.
        modelBuilder.Entity<User>(user =>
        {
            user.ToTable("users", table =>
            {
                // The database must reject a negative balance regardless of application
                // mistakes, so this constraint is the final line of defense.
                table.HasCheckConstraint(
                    "CK_users_credit_balance_not_negative",
                    "credit_balance >= 0");
            });

            user.HasKey(u => u.Id);
            user.Property(u => u.Id).HasColumnName("id");

            user.Property(u => u.Email)
                .HasColumnName("email")
                .HasMaxLength(320)
                .IsRequired();

            user.Property(u => u.PasswordHash)
                .HasColumnName("password_hash")
                .IsRequired();

            user.Property(u => u.CreditBalance)
                .HasColumnName("credit_balance")
                .IsRequired()
                .HasDefaultValue(0);

            user.Property(u => u.CreatedAt)
                .HasColumnName("created_at")
                .HasDefaultValueSql("now()")
                .ValueGeneratedOnAdd();

            // No two accounts may share an email address.
            user.HasIndex(u => u.Email).IsUnique();
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

        modelBuilder.Entity<KeyDrop>(keyDrop =>
        {
            keyDrop.ToTable("key_drops");

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

        modelBuilder.Entity<SteamKey>(steamKey =>
        {
            steamKey.ToTable("steam_keys");

            steamKey.HasKey(k => k.Id);
            steamKey.Property(k => k.Id).HasColumnName("id");

            steamKey.Property(k => k.DropId)
                .HasColumnName("drop_id")
                .IsRequired();

            steamKey.Property(k => k.KeyValue)
                .HasColumnName("key_value")
                .IsRequired();

            steamKey.Property(k => k.CreatedAt)
                .HasColumnName("created_at")
                .HasDefaultValueSql("now()")
                .ValueGeneratedOnAdd();

            steamKey.HasOne(k => k.Drop)
                .WithMany()
                .HasForeignKey(k => k.DropId)
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

            keyGrant.HasOne<SteamKey>()
                .WithMany()
                .HasForeignKey(g => g.KeyId)
                .OnDelete(DeleteBehavior.Cascade);

            // A user can receive only one key from a drop.
            keyGrant.HasIndex(g => new { g.DropId, g.UserId }).IsUnique();

            // A Steam key can be granted to only one user.
            keyGrant.HasIndex(g => g.KeyId).IsUnique();
        });

        // Credit history is append-only: rows are never updated or deleted, and
        // a user's balance is the sum of all of their delta values.
        modelBuilder.Entity<CreditLedgerEntry>(creditLedgerEntry =>
        {
            creditLedgerEntry.ToTable("credit_ledger_entries");

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

            // The ledger screen reads one user's history in this order.
            creditLedgerEntry.HasIndex(e => e.UserId);
        });

        modelBuilder.Entity<ReviewRequest>(reviewRequest =>
        {
            reviewRequest.ToTable("review_requests");

            reviewRequest.HasKey(r => r.Id);
            reviewRequest.Property(r => r.Id).HasColumnName("id");

            reviewRequest.Property(r => r.GameId)
                .HasColumnName("game_id")
                .IsRequired();

            reviewRequest.Property(r => r.RequesterId)
                .HasColumnName("requester_id")
                .IsRequired();

            reviewRequest.Property(r => r.SpendEntryId)
                .HasColumnName("spend_entry_id")
                .IsRequired();

            reviewRequest.Property(r => r.Status)
                .HasColumnName("status")
                .HasConversion<string>()
                .IsRequired()
                .HasDefaultValue(ReviewRequestStatus.Queued);

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
                .HasForeignKey(r => r.SpendEntryId)
                .OnDelete(DeleteBehavior.Cascade);

            // One credit spend creates exactly one request, so the database rejects
            // a second request even if application code tries to create it.
            reviewRequest.HasIndex(r => r.SpendEntryId).IsUnique();

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
            reviewAssignment.ToTable("review_assignments");

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

            reviewAssignment.Property(a => a.Status)
                .HasColumnName("status")
                .HasConversion<string>()
                .IsRequired();

            reviewAssignment.Property(a => a.LeasedUntil)
                .HasColumnName("leased_until")
                .IsRequired();

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
                .HasForeignKey(s => s.AssignmentId)
                .OnDelete(DeleteBehavior.Cascade);

            // One assignment produces at most one submission, so a second submit cannot create a
            // second row even if the conditional update above is somehow bypassed.
            feedbackSubmission.HasIndex(s => s.AssignmentId).IsUnique();
        });
    }
}
