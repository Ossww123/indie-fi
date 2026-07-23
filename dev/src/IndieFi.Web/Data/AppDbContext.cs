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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Names are mapped explicitly to snake_case: PostgreSQL folds unquoted
        // identifiers to lower case, and this project writes raw SQL by hand.
        modelBuilder.Entity<User>(user =>
        {
            user.ToTable("users");

            user.HasKey(u => u.Id);
            user.Property(u => u.Id).HasColumnName("id");

            user.Property(u => u.Email)
                .HasColumnName("email")
                .HasMaxLength(320)
                .IsRequired();

            user.Property(u => u.PasswordHash)
                .HasColumnName("password_hash")
                .IsRequired();

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
    }
}
