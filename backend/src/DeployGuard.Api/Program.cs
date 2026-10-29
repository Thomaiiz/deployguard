using System.Text.Json.Serialization;
using DeployGuard.Api.Alerting;
using DeployGuard.Api.Applications;
using DeployGuard.Api.Auth;
using DeployGuard.Api.Data;
using DeployGuard.Api.Deployments;
using DeployGuard.Api.History;
using DeployGuard.Api.Incidents;
using DeployGuard.Api.Infrastructure;
using DeployGuard.Api.Live;
using DeployGuard.Api.Monitoring;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// ---------- Configuração e segredos ----------
// A connection string NUNCA fica no repositório. Ela vem de variável de ambiente
// (ConnectionStrings__DeployGuard) ou do "user-secrets" do .NET em desenvolvimento.
var connectionString = builder.Configuration.GetConnectionString("DeployGuard")
    ?? throw new InvalidOperationException(
        "Connection string 'DeployGuard' não configurada. Use a variável ConnectionStrings__DeployGuard ou dotnet user-secrets.");

builder.Services.AddDbContext<DeployGuardDbContext>(options => options.UseNpgsql(connectionString));

// TimeProvider abstrai o relógio: em produção é o relógio real, nos testes podemos controlá-lo.
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddMonitoring(builder.Configuration);
builder.Services.AddAlerting(builder.Configuration);
builder.Services.AddSingleton<ApplicationValidator>();
builder.Services.AddApiRateLimiting(builder.Configuration);
builder.Services.AddDeployGuardAuth(builder.Configuration);
builder.Services.AddOptions<LiveOptions>()
    .Bind(builder.Configuration.GetSection(LiveOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddSingleton<LiveEventHub>();
builder.Services.AddSingleton<LiveEventBus>();
builder.Services.AddSingleton<LiveEventListener>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<LiveEventListener>());
builder.Services.AddOptions<DeploymentOptions>()
    .Bind(builder.Configuration.GetSection(DeploymentOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.ConfigureHttpJsonOptions(json =>
    json.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// Erros no formato padrão RFC 9457 (Problem Details), sem stack trace para o cliente.
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();

// Limita o tamanho do corpo das requisições: o maior payload legítimo tem poucos KB.
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = 32 * 1024);

// Atrás de um proxy reverso (nginx), o IP do cliente vem no cabeçalho X-Forwarded-For.
// Só confiamos nesse cabeçalho quando ele vem de redes conhecidas; senão qualquer
// cliente poderia forjar o IP e escapar do rate limit.
builder.Services.Configure<ForwardedHeadersOptions>(forwarded =>
{
    forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    foreach (var cidr in builder.Configuration.GetSection("ReverseProxy:KnownNetworks").Get<string[]>() ?? [])
    {
        forwarded.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(cidr));
    }
});

var app = builder.Build();

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseApiSecurityHeaders();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous(); // documento OpenAPI em /openapi/v1.json
}

if (app.Configuration.GetValue("Database:MigrateOnStartup", false))
{
    // Conveniente para dev e Docker Compose. Em produção o ideal é aplicar
    // migrations num passo separado do pipeline de deploy.
    await using var scope = app.Services.CreateAsyncScope();
    await DatabaseStartup.MigrateAsync(scope.ServiceProvider.GetRequiredService<DeployGuardDbContext>());
}

await AuthSetup.EnsureBootstrapAdminAsync(app.Services, app.Logger);

app.MapHealthChecks("/health").AllowAnonymous();

var api = app.MapGroup("/api").RequireRateLimiting(RateLimitingSetup.ReadPolicy);
api.MapAuthEndpoints();
api.MapUserEndpoints();
api.MapApiKeyEndpoints();
api.MapAuditEndpoints();
api.MapApplicationEndpoints();
api.MapHistoryEndpoints();
api.MapIncidentEndpoints();
api.MapDeploymentEndpoints();
api.MapLiveEndpoints();

app.Run();

// Torna a classe Program visível para o WebApplicationFactory dos testes de integração.
public partial class Program;
