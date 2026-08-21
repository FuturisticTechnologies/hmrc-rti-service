using Hmrc.Rti.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Payroll.Platform;

namespace Hmrc.Rti.Api.Infrastructure;

/// <summary>
/// Lives in the `rti` schema of the shared payroll database, alongside the
/// Python service's `payroll`, `audit` and `reference` schemas. One database,
/// separate schemas - so a pay run and its RTI submission can be reconciled in
/// a single query, while ownership of the tables stays unambiguous.
///
/// Tenant isolation is enforced by row-level security in the migration. The
/// global query filters here are defence in depth, not the primary control.
/// </summary>
public sealed class RtiDbContext(DbContextOptions<RtiDbContext> options, TenantContext tenant)
    : DbContext(options)
{
    public const string Schema = "rti";

    public DbSet<RtiSubmission> Submissions => Set<RtiSubmission>();
    public DbSet<RtiSubmissionEvent> SubmissionEvents => Set<RtiSubmissionEvent>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);

        b.Entity<RtiSubmission>(e =>
        {
            e.ToTable("rti_submissions");
            e.HasKey(x => x.Id);

            e.Property(x => x.TenantId).IsRequired();
            e.Property(x => x.SubmissionType).HasConversion<string>().HasMaxLength(8).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
            e.Property(x => x.TaxYear).IsRequired();
            e.Property(x => x.PeriodNumber).IsRequired();
            e.Property(x => x.EmployerPayeReference).HasMaxLength(32).IsRequired();
            e.Property(x => x.AccountsOfficeReference).HasMaxLength(32).IsRequired();
            e.Property(x => x.CorrelationId).HasMaxLength(64).IsRequired();
            e.Property(x => x.Payload).HasColumnType("jsonb").IsRequired();
            e.Property(x => x.PayloadHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.HmrcSubmissionId).HasMaxLength(128);
            e.Property(x => x.LastError).HasMaxLength(2000);

            // Money: never float. Matches the Decimal discipline in the Python engine.
            e.Property(x => x.TotalPayeTax).HasPrecision(14, 2);
            e.Property(x => x.TotalEmployeeNi).HasPrecision(14, 2);
            e.Property(x => x.TotalEmployerNi).HasPrecision(14, 2);

            e.HasIndex(x => new { x.TenantId, x.TaxYear, x.PeriodNumber });
            e.HasIndex(x => x.CorrelationId).IsUnique();

            // One accepted FPS per pay run. Partial index so superseded or failed
            // attempts against the same pay run remain legal.
            e.HasIndex(x => new { x.TenantId, x.PayRunId })
                .HasFilter("status = 'Accepted' AND submission_type = 'Fps'")
                .IsUnique()
                .HasDatabaseName("ix_rti_submissions_unique_accepted_fps");

            e.HasMany(x => x.Events)
                .WithOne()
                .HasForeignKey(x => x.SubmissionId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasQueryFilter(x => x.TenantId == tenant.TenantId);
        });

        b.Entity<RtiSubmissionEvent>(e =>
        {
            e.ToTable("rti_submission_events");
            e.HasKey(x => x.Id);

            e.Property(x => x.TenantId).IsRequired();
            e.Property(x => x.EventType).HasMaxLength(64).IsRequired();
            e.Property(x => x.FromStatus).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.ToStatus).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Detail).HasMaxLength(2000).IsRequired();

            e.HasIndex(x => new { x.SubmissionId, x.OccurredAt });
            e.HasQueryFilter(x => x.TenantId == tenant.TenantId);
        });

        base.OnModelCreating(b);
    }
}
