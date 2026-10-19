using Kaimo_File_Server.Core.Domain.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Kaimo_File_Server.Infrastructure.Persistence;

public partial class ApplicationDbContext
{
    /// <summary>Mail notifications: events, deliveries, rules and templates.</summary>
    private void ConfigureMail(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NotificationEvent>(entity =>
        {
            entity.ToTable("notification_events");
            entity.HasKey(e => e.Seq);
            // Database-assigned monotonic identity (Npgsql IDENTITY; SQLite AUTOINCREMENT).
            entity.Property(e => e.Seq).ValueGeneratedOnAdd();
            entity.Property(e => e.Type).IsRequired().HasMaxLength(100);
            entity.Property(e => e.SubjectUserIdsJson).IsRequired().HasColumnType("text");
            entity.Property(e => e.PayloadJson).IsRequired().HasColumnType("text");
            entity.Property(e => e.DedupKey).HasMaxLength(300);
            entity.Property(e => e.Status).HasConversion<int>();
            entity.Property(e => e.LastError).HasMaxLength(1000);
            entity.HasIndex(e => new { e.Status, e.Seq });
        });

        modelBuilder.Entity<MailDelivery>(entity =>
        {
            entity.ToTable("mail_deliveries");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.EventType).IsRequired().HasMaxLength(100);
            entity.Property(e => e.DedupKey).HasMaxLength(300);
            entity.Property(e => e.ToAddress).IsRequired().HasMaxLength(320);
            entity.Property(e => e.Language).IsRequired().HasMaxLength(10);
            entity.Property(e => e.Subject).IsRequired().HasMaxLength(1000);
            entity.Property(e => e.HtmlBody).IsRequired().HasColumnType("text");
            entity.Property(e => e.TextBody).IsRequired().HasColumnType("text");
            entity.Property(e => e.Status).HasConversion<int>();
            entity.Property(e => e.LastError).HasMaxLength(1000);
            entity.HasIndex(e => new { e.Status, e.NextAttemptUtc });
            entity.HasIndex(e => new { e.RuleId, e.DedupKey, e.ToAddress, e.CreatedAtUtc });
            entity.HasIndex(e => e.CreatedAtUtc);
        });

        modelBuilder.Entity<MailRule>(entity =>
        {
            entity.ToTable("mail_rules");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.EventType).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.RecipientsJson).IsRequired().HasColumnType("text");
            entity.HasIndex(e => e.EventType);
        });

        modelBuilder.Entity<MailTemplate>(entity =>
        {
            entity.ToTable("mail_templates");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.EventType).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Language).IsRequired().HasMaxLength(10);
            entity.Property(e => e.SubjectTemplate).IsRequired().HasMaxLength(1000);
            entity.Property(e => e.HtmlTemplate).IsRequired().HasColumnType("text");
            entity.Property(e => e.TextTemplate).HasColumnType("text");
            entity.HasIndex(e => new { e.EventType, e.Language }).IsUnique();
        });
    }
}
