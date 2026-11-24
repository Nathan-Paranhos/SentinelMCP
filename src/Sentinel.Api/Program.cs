using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.AspNetCore;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Sentinel.Api.Admin;
using Sentinel.Api.Auth;
using Sentinel.Api.Mcp;
using Sentinel.Api.Operacao;
using Sentinel.Api.WebReview;
using Sentinel.Core.CasosDeUso;
using Sentinel.Core.Contratos;
using Sentinel.Persistence.EmMemoria;
using Sentinel.Persistence.PostgreSql;

var ehStdio = args.Contains("--stdio");

if (ehStdio)
{
    // Reaproveita ASPNETCORE_ENVIRONMENT e garante ContentRoot válido mesmo quando iniciado de outro diretório
    var envStdio = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
        ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
    var raizStdio = File.Exists(Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json"))
        ? Directory.GetCurrentDirectory()
        : AppContext.BaseDirectory;
    var hostBuilder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
    {
        Args = args,
        EnvironmentName = string.IsNullOrWhiteSpace(envStdio) ? null : envStdio,
        ContentRootPath = raizStdio
    });
    hostBuilder.Logging.ClearProviders();
    hostBuilder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

    RegistrarPersistencia(hostBuilder.Services, hostBuilder.Configuration, hostBuilder.Environment, args);
    hostBuilder.Services.AddSingleton<ServicoDeMemoria>();
    hostBuilder.Services.AddSingleton<IServicoIdentidadeAgente, ServicoIdentidadeAgente>();
    hostBuilder.Services.AddSingleton<McpDispatcher>();
    hostBuilder.Services.AddSingleton<IdentidadeAmbienteStdio>();
    hostBuilder.Services
        .AddMcpServer(McpSdkAdapter.ConfigureMcpServerOptions)
        .ConfigureSentinelMcp()
        .WithStdioServerTransport();

    var host = hostBuilder.Build();
    await ValidarBancoInicialAsync(host.Services, hostBuilder.Configuration, hostBuilder.Environment, args, host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Sentinel.Stdio"));

    var tokenStdio = ObterArgumento(args, "--token") ?? Environment.GetEnvironmentVariable("SENTINEL_MCP_TOKEN");
    if (string.IsNullOrWhiteSpace(tokenStdio) && hostBuilder.Environment.IsDevelopment())
        tokenStdio = "mock:agent-dev-1|read:memory,write:decision,write:incident|codex-stdio";

    if (string.IsNullOrWhiteSpace(tokenStdio))
        throw new InvalidOperationException("Modo --stdio exige token em SENTINEL_MCP_TOKEN ou --token <valor>.");

    var authStdio = host.Services.GetRequiredService<IServicoIdentidadeAgente>() as ServicoIdentidadeAgente
        ?? throw new InvalidOperationException("Serviço de identidade padrão indisponível no modo STDIO.");
    host.Services.GetRequiredService<IdentidadeAmbienteStdio>().Atual = await authStdio.AutenticarBearerAsync(tokenStdio);

    await host.RunAsync();
    return;
}

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;
var ambiente = builder.Environment;

if (!ambiente.IsDevelopment())
    builder.Logging.AddJsonConsole();

// Observabilidade e métricas (OpenTelemetry)
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r
        .AddService(serviceName: SentinelDiagnostics.ServiceName, serviceVersion: SentinelDiagnostics.ServiceVersion)
        .AddAttributes(new Dictionary<string, object>
        {
            ["deployment.environment"] = ambiente.EnvironmentName
        }))
    .WithTracing(tracing =>
    {
        tracing
            .AddSource(SentinelDiagnostics.ServiceName)
            .AddAspNetCoreInstrumentation(o =>
            {
                o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/healthz");
            })
            .AddHttpClientInstrumentation();

        var otlp = config["OTEL_EXPORTER_OTLP_ENDPOINT"] ?? config["Sentinel:OpenTelemetry:OtlpEndpoint"];
        if (!string.IsNullOrEmpty(otlp))
            tracing.AddOtlpExporter(o => o.Endpoint = new Uri(otlp));
    })
    .WithMetrics(metrics =>
    {
        metrics
            .AddMeter(SentinelDiagnostics.ServiceName)
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddRuntimeInstrumentation();

        var otlp = config["OTEL_EXPORTER_OTLP_ENDPOINT"] ?? config["Sentinel:OpenTelemetry:OtlpEndpoint"];
        if (!string.IsNullOrEmpty(otlp))
            metrics.AddOtlpExporter(o => o.Endpoint = new Uri(otlp));
    });

// Persistência de dados
var provedor = RegistrarPersistencia(builder.Services, config, ambiente, args);

// Autenticação
var cookieSeguro = ambiente.IsProduction() ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
var autenticacao = builder.Services.AddAuthentication();

autenticacao.AddCookie(EsquemasDeAutenticacao.CookieWeb, o =>
{
    o.Cookie.Name = "sentinel_session";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Lax;
    o.Cookie.SecurePolicy = cookieSeguro;
    o.ExpireTimeSpan = TimeSpan.FromHours(8);
    o.SlidingExpiration = true;
    o.LoginPath = "/auth/login";
    o.Events.OnRedirectToLogin = ctx =>
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    };
});

if (WebReviewHandler.OidcConfigurado(config))
{
    autenticacao.AddOpenIdConnect(EsquemasDeAutenticacao.OidcWeb, o =>
    {
        o.Authority = config["Authentication:Web:Authority"];
        o.ClientId = config["Authentication:Web:ClientId"];
        o.ClientSecret = config["Authentication:Web:ClientSecret"];
        o.ResponseType = "code";
        o.UsePkce = true;
        o.SaveTokens = false;
        o.MapInboundClaims = false;
        o.SignInScheme = EsquemasDeAutenticacao.CookieWeb;
        o.CallbackPath = "/auth/callback";
        o.GetClaimsFromUserInfoEndpoint = true;
        o.RequireHttpsMetadata = !ambiente.IsDevelopment();
        o.Scope.Clear();
        o.Scope.Add("openid");
        o.Scope.Add("profile");
        o.TokenValidationParameters.NameClaimType = "name";
        o.Events.OnTokenValidated = ctx =>
        {
            // Garante claims de emissor e sujeito
            if (ctx.Principal?.Identity is ClaimsIdentity identidade && identidade.FindFirst("iss") == null)
                identidade.AddClaim(new Claim("iss", config["Authentication:Web:Issuer"] ?? ctx.Options.Authority!));
            return Task.CompletedTask;
        };
    });
}

if (ServicoIdentidadeAgente.JwtConfigurado(config))
{
    autenticacao.AddJwtBearer(EsquemasDeAutenticacao.JwtMcp, o =>
    {
        o.Authority = config["Authentication:Mcp:Authority"];
        o.Audience = config["Authentication:Mcp:Audience"];
        o.RequireHttpsMetadata = !ambiente.IsDevelopment();
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    });
}

builder.Services.AddAntiforgery(o =>
{
    o.HeaderName = "X-CSRF-Token";
    o.Cookie.Name = "sentinel_csrf";
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = cookieSeguro;
});

// Limites de taxa
static string ChaveCliente(HttpContext ctx)
{
    var autorizacao = ctx.Request.Headers.Authorization.ToString();
    if (!string.IsNullOrEmpty(autorizacao))
        return "cred:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(autorizacao)))[..16];
    return "ip:" + (ctx.Connection.RemoteIpAddress?.ToString() ?? "desconhecido");
}

FixedWindowRateLimiterOptions Janela(int limite) => new()
{
    PermitLimit = limite,
    Window = TimeSpan.FromMinutes(1),
    QueueLimit = 0
};

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("mcp", ctx => RateLimitPartition.GetFixedWindowLimiter(ChaveCliente(ctx), _ => Janela(config.GetValue("Sentinel:RateLimit:McpPerMinute", 300))));
    o.AddPolicy("web", ctx => RateLimitPartition.GetFixedWindowLimiter(ChaveCliente(ctx), _ => Janela(config.GetValue("Sentinel:RateLimit:WebPerMinute", 120))));
    o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter("ip:" + ctx.Connection.RemoteIpAddress, _ => Janela(config.GetValue("Sentinel:RateLimit:LoginPerMinute", 20))));
});

// Registro de serviços da aplicação
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<ServicoDeMemoria>();
builder.Services.AddSingleton<IServicoIdentidadeAgente, ServicoIdentidadeAgente>();
builder.Services.AddSingleton<McpDispatcher>();
builder.Services.AddSingleton<McpEndpointHandler>();
builder.Services.AddSingleton<AdminEndpointHandler>();
builder.Services.AddSingleton<WebReviewHandler>();
builder.Services
    .AddMcpServer(McpSdkAdapter.ConfigureMcpServerOptions)
    .ConfigureSentinelMcp()
    .WithHttpTransport(o =>
    {
        o.Stateless = false;
    });

if (config.GetValue("Sentinel:Retention:Enabled", true))
    builder.Services.AddHostedService<ServicoRetencao>();

var app = builder.Build();
var log = app.Logger;

// Comandos de inicialização e migrações
if (await ExecutarComandosBancoAsync(app.Services, config, ambiente, args, log))
    return;

// Pipeline HTTP e middlewares
const string PoliticaCsp =
    "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; " +
    "frame-ancestors 'none'; base-uri 'self'; form-action 'self'; object-src 'none'";

app.UseExceptionHandler(erro => erro.Run(async ctx =>
{
    var excecao = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
    log.LogError(excecao, "Erro não tratado em {Caminho} (correlação {Correlacao})", ctx.Request.Path, ctx.TraceIdentifier);
    ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
    await ctx.Response.WriteAsJsonAsync(new { code = "INTERNAL_ERROR", message = "Erro interno.", traceId = ctx.TraceIdentifier });
}));

if (!ambiente.IsDevelopment())
    app.UseHsts();

app.Use(async (ctx, next) =>
{
    ctx.Response.OnStarting(() =>
    {
        var h = ctx.Response.Headers;
        h.XContentTypeOptions = "nosniff";
        h["Referrer-Policy"] = "no-referrer";
        h.XFrameOptions = "DENY";
        var html = ctx.Response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true;
        if (html)
            h.ContentSecurityPolicy = PoliticaCsp;
        return Task.CompletedTask;
    });
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseRateLimiter();

app.MapGet("/healthz", () => Results.Ok(new { status = "Healthy" }));
app.MapGet("/readyz", async (IVerificadorProntidao verificador, CancellationToken ct) =>
{
    try
    {
        await verificador.VerificarAsync(ct);
        return Results.Ok(new { status = "Ready", storage = provedor });
    }
    catch (Exception ex)
    {
        log.LogWarning(ex, "Verificação de prontidão falhou.");
        return Results.Json(new { status = "NotReady" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

if (ServicoIdentidadeAgente.JwtConfigurado(config))
{
    // Metadados OAuth para clientes MCP (RFC 9728)
    app.MapGet("/.well-known/oauth-protected-resource", (HttpContext ctx) => Results.Ok(new
    {
        resource = $"{ctx.Request.Scheme}://{ctx.Request.Host}/mcp",
        authorization_servers = new[] { config["Authentication:Mcp:Authority"] },
        scopes_supported = Escopos.Todos,
        bearer_methods_supported = new[] { "header" }
    }));
}

app.MapMethods("/mcp", ["GET", "POST", "DELETE"], (HttpContext ctx, McpEndpointHandler handler) => handler.HandleAsync(ctx))
    .RequireRateLimiting("mcp");

app.UseWhen(ctx => ctx.Request.Path.StartsWithSegments("/mcp/sse"), branch =>
{
    branch.Use(McpSdkAdapter.AutenticarHttpAsync);
});
app.MapMcp("/mcp/sse").RequireRateLimiting("mcp");

app.MapGet("/api/admin/workspaces", (HttpContext ctx, AdminEndpointHandler h) => h.HandleListWorkspacesAsync(ctx)).RequireRateLimiting("web");
app.MapPost("/api/admin/workspaces", (HttpContext ctx, AdminEndpointHandler h) => h.HandleCreateWorkspaceAsync(ctx)).RequireRateLimiting("web");
app.MapGet("/api/admin/workspaces/{workspaceId:guid}", (HttpContext ctx, Guid workspaceId, AdminEndpointHandler h) => h.HandleGetWorkspaceAsync(ctx, workspaceId)).RequireRateLimiting("web");
app.MapGet("/api/admin/workspaces/{workspaceId:guid}/projects", (HttpContext ctx, Guid workspaceId, AdminEndpointHandler h) => h.HandleListProjectsAsync(ctx, workspaceId)).RequireRateLimiting("web");
app.MapPost("/api/admin/workspaces/{workspaceId:guid}/projects", (HttpContext ctx, Guid workspaceId, AdminEndpointHandler h) => h.HandleCreateProjectAsync(ctx, workspaceId)).RequireRateLimiting("web");
app.MapGet("/api/admin/workspaces/{workspaceId:guid}/memberships", (HttpContext ctx, Guid workspaceId, AdminEndpointHandler h) => h.HandleListMembershipsAsync(ctx, workspaceId)).RequireRateLimiting("web");
app.MapPost("/api/admin/workspaces/{workspaceId:guid}/memberships", (HttpContext ctx, Guid workspaceId, AdminEndpointHandler h) => h.HandleConfigureMembershipAsync(ctx, workspaceId)).RequireRateLimiting("web");
app.MapDelete("/api/admin/workspaces/{workspaceId:guid}/memberships/{membershipId:guid}", (HttpContext ctx, Guid workspaceId, Guid membershipId, AdminEndpointHandler h) => h.HandleRemoveMembershipAsync(ctx, workspaceId, membershipId)).RequireRateLimiting("web");

app.MapGet("/review", (HttpContext ctx, WebReviewHandler h) => h.RenderReviewPageAsync(ctx)).RequireRateLimiting("web");
app.MapGet("/app", (HttpContext ctx, WebReviewHandler h) => h.RenderReviewPageAsync(ctx)).RequireRateLimiting("web");
app.MapGet("/auth/login", (HttpContext ctx, WebReviewHandler h) => h.RenderLoginAsync(ctx)).RequireRateLimiting("login");
app.MapPost("/auth/dev-login", (HttpContext ctx, WebReviewHandler h) => h.HandleDevLoginAsync(ctx)).RequireRateLimiting("login");
app.MapPost("/auth/logout", (HttpContext ctx, WebReviewHandler h) => h.HandleLogoutAsync(ctx)).RequireRateLimiting("web");
app.MapGet("/api/review/state", (HttpContext ctx, WebReviewHandler h) => h.HandleStateAsync(ctx)).RequireRateLimiting("web");
app.MapGet("/api/review/timeline", (HttpContext ctx, WebReviewHandler h) => h.HandleTimelineAsync(ctx)).RequireRateLimiting("web");
app.MapGet("/api/review/receipt", (HttpContext ctx, WebReviewHandler h) => h.HandleReceiptAsync(ctx)).RequireRateLimiting("web");
app.MapPost("/api/review/approve", (HttpContext ctx, WebReviewHandler h) => h.HandleApproveAsync(ctx)).RequireRateLimiting("web");
app.MapPost("/api/review/reject", (HttpContext ctx, WebReviewHandler h) => h.HandleRejectAsync(ctx)).RequireRateLimiting("web");
app.MapPost("/api/review/restore", (HttpContext ctx, WebReviewHandler h) => h.HandleRestoreAsync(ctx)).RequireRateLimiting("web");

app.Run();

static string RegistrarPersistencia(IServiceCollection services, IConfiguration config, IHostEnvironment ambiente, string[] args)
{
    var provedor = config["Sentinel:StorageProvider"] ?? "InMemory";
    var usarPostgres = string.Equals(provedor, "PostgreSQL", StringComparison.OrdinalIgnoreCase);
    var stringConexao = config.GetConnectionString("Database");

    if (usarPostgres)
    {
        if (string.IsNullOrWhiteSpace(stringConexao))
            throw new InvalidOperationException("Sentinel:StorageProvider=PostgreSQL exige ConnectionStrings:Database (forneça por variável de ambiente ou cofre de segredos).");

        var fonte = NpgsqlDataSource.Create(stringConexao);
        var repositorio = new RepositorioSentinelPostgreSql(fonte);
        services.AddSingleton(fonte);
        services.AddSingleton(repositorio);
        RegistrarInterfaces(services, repositorio);
    }
    else
    {
        if (ambiente.IsProduction() && !config.GetValue<bool>("Sentinel:AllowInMemoryInProduction"))
            throw new InvalidOperationException("O armazenamento InMemory não persiste dados e não é permitido em Production. Configure Sentinel:StorageProvider=PostgreSQL.");

        var repositorio = new RepositorioSentinelEmMemoria();
        if (args.Contains("--seed-demo") || config.GetValue<bool>("Sentinel:SeedDemoData"))
            repositorio.PopularDadosDemonstracao();
        services.AddSingleton(repositorio);
        RegistrarInterfaces(services, repositorio);
    }

    return provedor;
}

static void RegistrarInterfaces<T>(IServiceCollection servicos, T repositorio) where T : class,
    IRepositorioEspacoTrabalho, IRepositorioDecisao, IRepositorioIncidente, IRepositorioContexto,
    IRepositorioHistorico, IReconstrutorProjecoes, IManutencaoRetencao, IVerificadorProntidao
{
    servicos.AddSingleton<IRepositorioEspacoTrabalho>(repositorio);
    servicos.AddSingleton<IRepositorioDecisao>(repositorio);
    servicos.AddSingleton<IRepositorioIncidente>(repositorio);
    servicos.AddSingleton<IRepositorioContexto>(repositorio);
    servicos.AddSingleton<IRepositorioHistorico>(repositorio);
    servicos.AddSingleton<IReconstrutorProjecoes>(repositorio);
    servicos.AddSingleton<IManutencaoRetencao>(repositorio);
    servicos.AddSingleton<IVerificadorProntidao>(repositorio);
}

static string DiretorioDb(string relativo) => Path.Combine(AppContext.BaseDirectory, "db", relativo);

static string? ObterArgumento(string[] args, string nome)
{
    var idx = Array.IndexOf(args, nome);
    return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
}

static async Task ValidarBancoInicialAsync(IServiceProvider services, IConfiguration config, IHostEnvironment ambiente, string[] args, ILogger log)
{
    var provedor = config["Sentinel:StorageProvider"] ?? "InMemory";
    if (!string.Equals(provedor, "PostgreSQL", StringComparison.OrdinalIgnoreCase))
        return;

    var stringConexao = config.GetConnectionString("Database");
    var stringConexaoAdmin = config.GetConnectionString("DatabaseAdmin") ?? stringConexao;

    if (args.Contains("--migrate") || config.GetValue<bool>("Sentinel:AutoMigrate"))
    {
        var aplicadas = await new MigradorBancoDados(stringConexaoAdmin!).MigrarAsync(DiretorioDb("migrations"));
        log.LogInformation("Migrações aplicadas: {Migracoes}", aplicadas.Count == 0 ? "nenhuma pendente" : string.Join(", ", aplicadas));

        if (aplicadas.Any(a => a.StartsWith("003_", StringComparison.Ordinal)))
        {
            await using var fonteAdmin = NpgsqlDataSource.Create(stringConexaoAdmin!);
            await new RepositorioSentinelPostgreSql(fonteAdmin).ReconstruirProjecoesAsync();
            log.LogInformation("Projeções reconstruídas após a migração 003.");
        }
    }

    var (papel, ignoraRls) = await services.GetRequiredService<RepositorioSentinelPostgreSql>().VerificarPrivilegiosAsync();
    if (ignoraRls)
    {
        if (ambiente.IsProduction() && !config.GetValue<bool>("Sentinel:Database:AllowPrivilegedRole"))
            throw new InvalidOperationException($"O usuário de banco '{papel}' ignora RLS (superusuário ou BYPASSRLS). Use um login membro de 'sentinel_app'.");
        log.LogWarning("O usuário de banco {Papel} ignora RLS; o isolamento depende apenas dos filtros da aplicação. Não use em produção.", papel);
    }
}

static async Task<bool> ExecutarComandosBancoAsync(IServiceProvider services, IConfiguration config, IHostEnvironment ambiente, string[] args, ILogger log)
{
    var provedor = config["Sentinel:StorageProvider"] ?? "InMemory";
    var usarPostgres = string.Equals(provedor, "PostgreSQL", StringComparison.OrdinalIgnoreCase);
    var stringConexao = config.GetConnectionString("Database");
    var stringConexaoAdmin = config.GetConnectionString("DatabaseAdmin") ?? stringConexao;

    if (usarPostgres)
    {
        if (args.Contains("--migrate") || config.GetValue<bool>("Sentinel:AutoMigrate"))
        {
            var aplicadas = await new MigradorBancoDados(stringConexaoAdmin!).MigrarAsync(DiretorioDb("migrations"));
            log.LogInformation("Migrações aplicadas: {Migracoes}", aplicadas.Count == 0 ? "nenhuma pendente" : string.Join(", ", aplicadas));

            if (aplicadas.Any(a => a.StartsWith("003_", StringComparison.Ordinal)))
            {
                await using var fonteAdmin = NpgsqlDataSource.Create(stringConexaoAdmin!);
                await new RepositorioSentinelPostgreSql(fonteAdmin).ReconstruirProjecoesAsync();
                log.LogInformation("Projeções reconstruídas após a migração 003.");
            }

            if (args.Contains("--migrate"))
                return true;
        }

        if (args.Contains("--seed-demo"))
        {
            await new MigradorBancoDados(stringConexaoAdmin!).AplicarFixturesAsync(DiretorioDb("fixtures"));
            await using var fonteAdmin = NpgsqlDataSource.Create(stringConexaoAdmin!);
            await new RepositorioSentinelPostgreSql(fonteAdmin).ReconstruirProjecoesAsync();
            log.LogInformation("Fixtures de demonstração aplicadas e projeções reconstruídas.");
            return true;
        }

        await ValidarBancoInicialAsync(services, config, ambiente, args, log);
    }

    if (args.Contains("--rebuild-projections"))
    {
        Guid? alvo = null;
        var indice = Array.IndexOf(args, "--workspace-id");
        if (indice >= 0)
        {
            if (indice + 1 >= args.Length || !Guid.TryParse(args[indice + 1], out var ws))
                throw new ArgumentException("--workspace-id exige um UUID.");
            alvo = ws;
        }
        await services.GetRequiredService<IReconstrutorProjecoes>().ReconstruirProjecoesAsync(alvo);
        log.LogInformation("Projeções reconstruídas ({Alvo}).", alvo?.ToString() ?? "todos os workspaces");
        return true;
    }

    return false;
}

public partial class Program { }
