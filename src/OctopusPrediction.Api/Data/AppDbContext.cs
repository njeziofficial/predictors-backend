using Microsoft.EntityFrameworkCore;
using OctopusPrediction.Api.Entities;

namespace OctopusPrediction.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<MatchWeek> MatchWeeks => Set<MatchWeek>();
    public DbSet<Fixture> Fixtures => Set<Fixture>();
    public DbSet<Prediction> Predictions => Set<Prediction>();
    public DbSet<ScraperSettings> ScraperSettings => Set<ScraperSettings>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<PreviousPoints> PreviousPoints => Set<PreviousPoints>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<PlayerAlias> PlayerAliases => Set<PlayerAlias>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<UserPermission> UserPermissions => Set<UserPermission>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ConversationParticipant> ConversationParticipants => Set<ConversationParticipant>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<User>(u =>
        {
            u.HasKey(x => x.Id);
            u.HasIndex(x => x.Email).IsUnique();
            u.Property(x => x.Email).HasMaxLength(256);
            u.Property(x => x.Name).HasMaxLength(200);
            u.Property(x => x.PhoneNumber).HasMaxLength(30);
            u.Property(x => x.WhatsAppName).HasMaxLength(200);
        });

        mb.Entity<MatchWeek>(w =>
        {
            w.HasKey(x => x.Id);
            w.Property(x => x.Id).HasMaxLength(150);
            w.Property(x => x.Name).HasMaxLength(200);
            w.Property(x => x.Competition).HasMaxLength(100);
        });

        mb.Entity<Fixture>(f =>
        {
            f.HasKey(x => x.Id);
            f.Property(x => x.Id).HasMaxLength(100);
            f.Property(x => x.WeekId).HasMaxLength(150);
            f.Property(x => x.HomeTeam).HasMaxLength(150);
            f.Property(x => x.AwayTeam).HasMaxLength(150);
            f.HasOne(x => x.Week)
             .WithMany(x => x.Fixtures)
             .HasForeignKey(x => x.WeekId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        mb.Entity<Prediction>(p =>
        {
            p.HasKey(x => x.Id);
            p.HasIndex(x => new { x.UserId, x.FixtureId }).IsUnique();
            p.Property(x => x.FixtureId).HasMaxLength(100);
            p.Property(x => x.WeekId).HasMaxLength(150);
            p.HasOne(x => x.User)
             .WithMany(x => x.Predictions)
             .HasForeignKey(x => x.UserId)
             .OnDelete(DeleteBehavior.Cascade);
            p.HasOne(x => x.Fixture)
             .WithMany(x => x.Predictions)
             .HasForeignKey(x => x.FixtureId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        mb.Entity<ScraperSettings>(s =>
        {
            s.HasKey(x => x.Id);
            s.Property(x => x.Competition).HasMaxLength(100);
            s.Property(x => x.SourceName).HasMaxLength(50);
            s.Property(x => x.SourceMode).HasMaxLength(20);
            s.Property(x => x.SourceOrder).HasMaxLength(500);
        });

        mb.Entity<AuditLog>(a =>
        {
            a.HasKey(x => x.Id);
            a.Property(x => x.UserName).HasMaxLength(200);
            a.Property(x => x.ActorName).HasMaxLength(200);
            a.Property(x => x.Action).HasMaxLength(50);
            a.Property(x => x.Field).HasMaxLength(50);
            a.Property(x => x.PreviousValue).HasMaxLength(500);
            a.Property(x => x.NewValue).HasMaxLength(500);
            a.Property(x => x.Details).HasMaxLength(500);
            a.HasIndex(x => x.CreatedAt);
        });

        mb.Entity<RefreshToken>(t =>
        {
            t.HasKey(x => x.Id);
            t.Property(x => x.TokenHash).HasMaxLength(64);
            t.Property(x => x.RevokedReason).HasMaxLength(50);
            t.HasIndex(x => x.TokenHash).IsUnique();
            t.HasIndex(x => x.UserId);
            t.HasIndex(x => x.FamilyId);
            t.HasOne(x => x.User)
             .WithMany()
             .HasForeignKey(x => x.UserId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        mb.Entity<PreviousPoints>(pp =>
        {
            pp.HasKey(x => x.Id);
            pp.Property(x => x.Label).HasMaxLength(100);
            pp.HasIndex(x => new { x.UserId, x.Label }).IsUnique();
            pp.HasOne(x => x.User)
              .WithMany(x => x.PreviousPoints)
              .HasForeignKey(x => x.UserId)
              .OnDelete(DeleteBehavior.Cascade);
        });

        mb.Entity<PlayerAlias>(a =>
        {
            a.HasKey(x => x.Id);
            a.Property(x => x.Key).HasMaxLength(200);
            a.Property(x => x.Alias).HasMaxLength(200);
            a.HasIndex(x => x.Key).IsUnique();
            a.HasOne(x => x.User)
              .WithMany()
              .HasForeignKey(x => x.UserId)
              .OnDelete(DeleteBehavior.Cascade);
        });

        mb.Entity<RolePermission>(r =>
        {
            r.HasKey(x => x.Permission);
            r.Property(x => x.Permission).HasMaxLength(64);
        });

        mb.Entity<UserPermission>(p =>
        {
            p.HasKey(x => new { x.UserId, x.Permission });
            p.Property(x => x.Permission).HasMaxLength(64);
            p.HasOne(x => x.User)
              .WithMany()
              .HasForeignKey(x => x.UserId)
              .OnDelete(DeleteBehavior.Cascade);
        });

        mb.Entity<Conversation>(c =>
        {
            c.HasKey(x => x.Id);
            c.Property(x => x.DirectKey).HasMaxLength(80);
            c.HasIndex(x => x.DirectKey).IsUnique();
        });

        mb.Entity<ConversationParticipant>(p =>
        {
            p.HasKey(x => new { x.ConversationId, x.UserId });
            p.HasIndex(x => x.UserId);
            p.HasOne(x => x.Conversation)
              .WithMany(x => x.Participants)
              .HasForeignKey(x => x.ConversationId)
              .OnDelete(DeleteBehavior.Cascade);
            p.HasOne(x => x.User)
              .WithMany()
              .HasForeignKey(x => x.UserId)
              .OnDelete(DeleteBehavior.Cascade);
        });

        mb.Entity<ChatMessage>(m =>
        {
            m.HasKey(x => x.Id);
            m.Property(x => x.Body).HasMaxLength(2000);
            m.Property(x => x.ClientId).HasMaxLength(64);
            m.HasIndex(x => new { x.ConversationId, x.CreatedAt });
            m.HasIndex(x => new { x.SenderId, x.ClientId }).IsUnique();
            m.HasOne(x => x.Conversation)
              .WithMany(x => x.Messages)
              .HasForeignKey(x => x.ConversationId)
              .OnDelete(DeleteBehavior.Cascade);
            m.HasOne(x => x.Sender)
              .WithMany()
              .HasForeignKey(x => x.SenderId)
              .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
