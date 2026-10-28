using DeployGuard.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace DeployGuard.Api.Data;

public sealed class DeployGuardDbContext(DbContextOptions<DeployGuardDbContext> options) : DbContext(options)
{
    public DbSet<MonitoredApplication> Applications => Set<MonitoredApplication>();

    public DbSet<HealthCheckResult> HealthCheckResults => Set<HealthCheckResult>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MonitoredApplication>(app =>
        {
            app.ToTable("applications");
            app.HasKey(a => a.Id);
            app.Property(a => a.Name).HasMaxLength(100).IsRequired();
            app.Property(a => a.HealthCheckUrl).HasMaxLength(2048).IsRequired();
            app.Property(a => a.LastStatus).HasConversion<string>().HasMaxLength(16);

            // O agendador procura "aplicações habilitadas cuja próxima verificação já venceu".
            app.HasIndex(a => new { a.IsEnabled, a.NextCheckAt });

            app.HasMany(a => a.Results)
                .WithOne()
                .HasForeignKey(r => r.ApplicationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<HealthCheckResult>(result =>
        {
            result.ToTable("health_check_results");
            result.HasKey(r => r.Id);
            result.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);
            result.Property(r => r.ErrorType).HasConversion<string>().HasMaxLength(32);
            result.Property(r => r.ErrorMessage).HasMaxLength(500);

            // Toda consulta de histórico filtra por aplicação e ordena por data.
            result.HasIndex(r => new { r.ApplicationId, r.CheckedAt }).IsDescending(false, true);
            // Usado pela limpeza de retenção (apaga resultados antigos).
            result.HasIndex(r => r.CheckedAt);
        });
    }
}
