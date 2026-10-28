using DeployGuard.Api.Data;
using DeployGuard.Api.Domain;
using DeployGuard.Api.Infrastructure;
using DeployGuard.Api.Monitoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DeployGuard.Api.Applications;

public static class ApplicationEndpoints
{
    public static RouteGroupBuilder MapApplicationEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/applications").WithTags("Applications");

        group.MapGet("/", ListAsync);
        group.MapGet("/{id:guid}", GetAsync).WithName("GetApplication");
        group.MapPost("/", CreateAsync).RequireRateLimiting(RateLimitingSetup.WritePolicy);
        group.MapPut("/{id:guid}", UpdateAsync).RequireRateLimiting(RateLimitingSetup.WritePolicy);
        group.MapDelete("/{id:guid}", DeleteAsync).RequireRateLimiting(RateLimitingSetup.WritePolicy);

        return api;
    }

    private static async Task<IResult> ListAsync(DeployGuardDbContext db, TimeProvider time, CancellationToken ct)
    {
        var apps = await db.Applications.AsNoTracking().OrderBy(a => a.Name).ToListAsync(ct);
        var availability = await AvailabilityQueries.Last24hByApplicationAsync(db, time.GetUtcNow(), ct);
        var openIncidents = await db.Incidents.AsNoTracking()
            .Where(i => i.ResolvedAt == null)
            .ToDictionaryAsync(i => i.ApplicationId, i => i.StartedAt, ct);

        return Results.Ok(apps.Select(a => ApplicationResponse.From(
            a, availability.GetValueOrDefault(a.Id), openIncidents.TryGetValue(a.Id, out var since) ? since : null)));
    }

    private static async Task<IResult> GetAsync(Guid id, DeployGuardDbContext db, TimeProvider time, CancellationToken ct)
    {
        var app = await db.Applications.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
        if (app is null)
        {
            return Results.NotFound();
        }

        var availability = await AvailabilityQueries.Last24hByApplicationAsync(db, time.GetUtcNow(), ct, id);
        var openSince = await db.Incidents.AsNoTracking()
            .Where(i => i.ApplicationId == id && i.ResolvedAt == null)
            .Select(i => (DateTimeOffset?)i.StartedAt)
            .FirstOrDefaultAsync(ct);

        return Results.Ok(ApplicationResponse.From(app, availability.GetValueOrDefault(id), openSince));
    }

    private static async Task<IResult> CreateAsync(
        CreateApplicationRequest request,
        DeployGuardDbContext db,
        ApplicationValidator validator,
        IOptionsMonitor<MonitoringOptions> options,
        TimeProvider time,
        CancellationToken ct)
    {
        var settings = options.CurrentValue;
        var interval = request.CheckIntervalSeconds ?? settings.DefaultIntervalSeconds;

        var errors = validator.Validate(request.Name, request.HealthCheckUrl, interval);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        // Limite de cadastros: cada aplicação gera tráfego de saída periódico.
        if (await db.Applications.CountAsync(ct) >= settings.MaxApplications)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Limite de aplicações atingido.",
                detail: $"O máximo configurado é {settings.MaxApplications}.");
        }

        var now = time.GetUtcNow();
        var app = new MonitoredApplication
        {
            Id = Guid.CreateVersion7(now),
            Name = request.Name!.Trim(),
            HealthCheckUrl = request.HealthCheckUrl!.Trim(),
            CheckIntervalSeconds = interval,
            CreatedAt = now,
            NextCheckAt = now, // a primeira verificação acontece no próximo ciclo do agendador
        };

        db.Applications.Add(app);
        await db.SaveChangesAsync(ct);

        return Results.CreatedAtRoute("GetApplication", new { id = app.Id }, ApplicationResponse.From(app, null));
    }

    private static async Task<IResult> UpdateAsync(
        Guid id,
        UpdateApplicationRequest request,
        DeployGuardDbContext db,
        ApplicationValidator validator,
        TimeProvider time,
        CancellationToken ct)
    {
        var app = await db.Applications.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (app is null)
        {
            return Results.NotFound();
        }

        var name = request.Name ?? app.Name;
        var url = request.HealthCheckUrl ?? app.HealthCheckUrl;
        var interval = request.CheckIntervalSeconds ?? app.CheckIntervalSeconds;

        var errors = validator.Validate(name, url, interval);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var urlChanged = !string.Equals(url.Trim(), app.HealthCheckUrl, StringComparison.Ordinal);

        app.Name = name.Trim();
        app.HealthCheckUrl = url.Trim();
        app.CheckIntervalSeconds = interval;
        app.IsEnabled = request.IsEnabled ?? app.IsEnabled;

        if (urlChanged)
        {
            // Estado anterior pertence à URL antiga; verifica a nova o quanto antes.
            app.LastStatus = null;
            app.LastLatencyMs = null;
            app.LastCheckedAt = null;
            app.ConsecutiveFailures = 0;
            app.NextCheckAt = time.GetUtcNow();
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(ApplicationResponse.From(app, null));
    }

    private static async Task<IResult> DeleteAsync(Guid id, DeployGuardDbContext db, CancellationToken ct)
    {
        // ExecuteDeleteAsync gera um DELETE direto, sem carregar a entidade.
        // O histórico é removido em cascata pela foreign key.
        var deleted = await db.Applications.Where(a => a.Id == id).ExecuteDeleteAsync(ct);
        return deleted == 0 ? Results.NotFound() : Results.NoContent();
    }
}
