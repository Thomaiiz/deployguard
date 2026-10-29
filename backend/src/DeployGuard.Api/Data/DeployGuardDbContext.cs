using DeployGuard.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace DeployGuard.Api.Data;

public sealed class DeployGuardDbContext(DbContextOptions<DeployGuardDbContext> options) : DbContext(options)
{
    public DbSet<MonitoredApplication> Applications => Set<MonitoredApplication>();

    public DbSet<HealthCheckResult> HealthCheckResults => Set<HealthCheckResult>();

    public DbSet<Incident> Incidents => Set<Incident>();

    public DbSet<AlertNotification> AlertNotifications => Set<AlertNotification>();

    public DbSet<User> Users => Set<User>();

    public DbSet<Deployment> Deployments => Set<Deployment>();

    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(user =>
        {
            user.ToTable("users");
            user.HasKey(u => u.Id);
            user.Property(u => u.Email).HasMaxLength(254).IsRequired();
            user.Property(u => u.PasswordHash).HasMaxLength(512).IsRequired();
            user.Property(u => u.Role).HasConversion<string>().HasMaxLength(16);
            user.HasIndex(u => u.Email).IsUnique();
        });

        modelBuilder.Entity<Deployment>(deployment =>
        {
            deployment.ToTable("deployments");
            deployment.HasKey(d => d.Id);
            deployment.Property(d => d.Version).HasMaxLength(100).IsRequired();
            deployment.Property(d => d.CommitSha).HasMaxLength(40);
            deployment.Property(d => d.Description).HasMaxLength(500);
            deployment.Property(d => d.Url).HasMaxLength(2048);
            deployment.Property(d => d.RegisteredBy).HasMaxLength(300).IsRequired();

            deployment.HasOne<MonitoredApplication>()
                .WithMany()
                .HasForeignKey(d => d.ApplicationId)
                .OnDelete(DeleteBehavior.Cascade);

            // Atende "últimos deploys da aplicação" e "deploy logo antes do incidente".
            deployment.HasIndex(d => new { d.ApplicationId, d.DeployedAt });
        });

        modelBuilder.Entity<ApiKey>(key =>
        {
            key.ToTable("api_keys");
            key.HasKey(k => k.Id);
            key.Property(k => k.Name).HasMaxLength(100).IsRequired();
            key.Property(k => k.Prefix).HasMaxLength(16).IsRequired();
            key.Property(k => k.KeyHash).HasMaxLength(64).IsRequired();
            key.Property(k => k.CreatedBy).HasMaxLength(254).IsRequired();
            key.HasIndex(k => k.KeyHash).IsUnique(); // a autenticação busca pela chave já "hasheada"
        });

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

        modelBuilder.Entity<Incident>(incident =>
        {
            incident.ToTable("incidents");
            incident.HasKey(i => i.Id);
            incident.Property(i => i.Cause).HasConversion<string>().HasMaxLength(32);
            incident.Property(i => i.Summary).HasMaxLength(500);

            incident.HasOne<MonitoredApplication>()
                .WithMany()
                .HasForeignKey(i => i.ApplicationId)
                .OnDelete(DeleteBehavior.Cascade);

            // Índice único PARCIAL: no máximo um incidente aberto por aplicação.
            // A regra de negócio vira garantia do banco, mesmo com bugs ou concorrência.
            incident.HasIndex(i => i.ApplicationId)
                .IsUnique()
                .HasFilter("\"ResolvedAt\" IS NULL")
                .HasDatabaseName("ux_incidents_one_open_per_application");

            incident.HasIndex(i => new { i.ApplicationId, i.StartedAt }).IsDescending(false, true);
        });

        modelBuilder.Entity<AlertNotification>(notification =>
        {
            notification.ToTable("alert_notifications");
            notification.HasKey(n => n.Id);
            notification.Property(n => n.EventType).HasConversion<string>().HasMaxLength(32);
            notification.Property(n => n.Channel).HasMaxLength(64);
            notification.Property(n => n.LastError).HasMaxLength(500);

            notification.HasOne(n => n.Incident)
                .WithMany(i => i.Notifications)
                .HasForeignKey(n => n.IncidentId)
                .OnDelete(DeleteBehavior.Cascade);

            // O dispatcher busca "pendentes cuja próxima tentativa já venceu".
            notification.HasIndex(n => n.NextAttemptAt).HasFilter("\"SentAt\" IS NULL AND \"FailedAt\" IS NULL");
        });
    }
}
