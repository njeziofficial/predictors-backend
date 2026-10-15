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
    public DbSet<MatchUpdate> MatchUpdates => Set<MatchUpdate>();
    public DbSet<Competition> Competitions => Set<Competition>();
    public DbSet<PushSubscription> PushSubscriptions => Set<PushSubscription>();
    public DbSet<PushKeys> PushKeys => Set<PushKeys>();
    public DbSet<PushBroadcast> PushBroadcasts => Set<PushBroadcast>();
    public DbSet<PasswordToken> PasswordTokens => Set<PasswordToken>();
    public DbSet<EmailLog> EmailLogs => Set<EmailLog>();
    public DbSet<UserNotification> UserNotifications => Set<UserNotification>();
    public DbSet<Passkey> Passkeys => Set<Passkey>();

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
            w.Property(x => x.CompetitionId).HasMaxLength(50);
            w.HasIndex(x => x.CompetitionId);
        });

        mb.Entity<Competition>(c =>
        {
            c.HasKey(x => x.Id);
            c.Property(x => x.Id).HasMaxLength(50);
            c.Property(x => x.Name).HasMaxLength(100);
            c.Property(x => x.SourceMode).HasMaxLength(20);
            c.Property(x => x.SourceName).HasMaxLength(50);
            c.Property(x => x.SourceOrder).HasMaxLength(500);
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
            s.Property(x => x.ReminderOffsetsHours).HasMaxLength(100);
            s.Property(x => x.EmailProvider).HasMaxLength(20);
            s.Property(x => x.EmailSenderName).HasMaxLength(100);
            s.Property(x => x.EmailResendFrom).HasMaxLength(256);
            s.Property(x => x.EmailBrevoSender).HasMaxLength(256);
            s.Property(x => x.EmailGmailSender).HasMaxLength(256);
        });

        mb.Entity<MatchUpdate>(u =>
        {
            u.HasKey(x => x.Id);
            u.Property(x => x.Id).UseIdentityAlwaysColumn();
            u.Property(x => x.FixtureId).HasMaxLength(100);
            u.Property(x => x.Source).HasMaxLength(50);
            u.Property(x => x.Competition).HasMaxLength(100);
            u.Property(x => x.Fingerprint).HasMaxLength(64);
            u.Property(x => x.Payload).HasColumnType("jsonb");
            u.Property(x => x.Outcome).HasMaxLength(20);
            u.Property(x => x.Error).HasMaxLength(500);
            u.HasIndex(x => x.ProcessedAt);
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

        mb.Entity<PushSubscription>(s =>
        {
            s.HasKey(x => x.Id);
            s.Property(x => x.Endpoint).HasMaxLength(1000);
            s.Property(x => x.P256dh).HasMaxLength(200);
            s.Property(x => x.Auth).HasMaxLength(100);
            s.HasIndex(x => x.Endpoint).IsUnique();
            s.HasIndex(x => x.UserId);
            s.HasOne(x => x.User)
              .WithMany()
              .HasForeignKey(x => x.UserId)
              .OnDelete(DeleteBehavior.Cascade);
        });

        mb.Entity<PushBroadcast>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Title).HasMaxLength(100);
            b.Property(x => x.Body).HasMaxLength(300);
            b.Property(x => x.Url).HasMaxLength(200);
            b.Property(x => x.Audience).HasMaxLength(20);
            b.Property(x => x.WeekId).HasMaxLength(150);
            b.Property(x => x.SentByName).HasMaxLength(200);
            b.HasIndex(x => x.CreatedAt);
        });

        mb.Entity<PasswordToken>(t =>
        {
            t.HasKey(x => x.Id);
            t.Property(x => x.TokenHash).HasMaxLength(64);
            t.Property(x => x.Purpose).HasMaxLength(20);
            t.HasIndex(x => x.TokenHash).IsUnique();
            t.HasIndex(x => x.UserId);
            t.HasOne(x => x.User)
              .WithMany()
              .HasForeignKey(x => x.UserId)
              .OnDelete(DeleteBehavior.Cascade);
        });

        mb.Entity<EmailLog>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Provider).HasMaxLength(20);
            e.Property(x => x.To).HasMaxLength(256);
            e.Property(x => x.Kind).HasMaxLength(20);
            e.Property(x => x.Subject).HasMaxLength(200);
            e.Property(x => x.Error).HasMaxLength(500);
            e.HasIndex(x => x.CreatedAt);
        });

        mb.Entity<UserNotification>(n =>
        {
            n.HasKey(x => x.Id);
            n.Property(x => x.Kind).HasMaxLength(20);
            n.Property(x => x.Title).HasMaxLength(100);
            n.Property(x => x.Body).HasMaxLength(300);
            n.Property(x => x.Url).HasMaxLength(200);
            n.HasIndex(x => new { x.UserId, x.CreatedAt });
            n.HasIndex(x => x.CreatedAt);
            n.HasOne(x => x.User)
              .WithMany()
              .HasForeignKey(x => x.UserId)
              .OnDelete(DeleteBehavior.Cascade);
        });

        mb.Entity<PushKeys>(k =>
        {
            k.HasKey(x => x.Id);
            k.Property(x => x.Id).ValueGeneratedNever();
            k.Property(x => x.PublicKey).HasMaxLength(200);
            k.Property(x => x.PrivateKey).HasMaxLength(200);
        });

        mb.Entity<Passkey>(p =>
        {
            p.HasKey(x => x.Id);
            p.Property(x => x.CredentialId).HasMaxLength(1024);
            p.Property(x => x.PublicKey).HasMaxLength(2048);
            p.Property(x => x.Name).HasMaxLength(60);
            p.Property(x => x.Transports).HasMaxLength(100);
            p.HasIndex(x => x.CredentialId).IsUnique();
            p.HasIndex(x => x.UserId);
            p.HasOne(x => x.User)
              .WithMany()
              .HasForeignKey(x => x.UserId)
              .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
