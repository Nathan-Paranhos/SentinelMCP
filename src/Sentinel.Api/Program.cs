using Sentinel.Api.Auth;
using Sentinel.Api.Mcp;
using Sentinel.Api.WebReview;
using Sentinel.Persistence.Contratos;
using Sentinel.Persistence.EmMemoria;
using Sentinel.Persistence.PostgreSql;

var builder = WebApplication.CreateBuilder(args);

var stringConexao = builder.Configuration.GetConnectionString("Database");
var provedorArmazenamento = builder.Configuration.GetValue<string>("Sentinel:StorageProvider") ?? "InMemory";

if (string.Equals(provedorArmazenamento, "PostgreSQL", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(stringConexao))
{
    var pgRepo = new RepositorioSentinelPostgreSql(stringConexao);
    builder.Services.AddSingleton<IRepositorioEspacoTrabalho>(pgRepo);
    builder.Services.AddSingleton<IRepositorioDecisao>(pgRepo);
    builder.Services.AddSingleton<IRepositorioIncidente>(pgRepo);
    builder.Services.AddSingleton<IRepositorioContexto>(pgRepo);
    builder.Services.AddSingleton<IRepositorioRecibo>(pgRepo);
    builder.Services.AddSingleton<IReconstrutorProjecoes>(pgRepo);
}
else
{
    var repositorioEmMemoria = new RepositorioSentinelEmMemoria();
    if (args.Contains("--seed-demo"))
    {
        repositorioEmMemoria.PopularDadosDemonstracao();
    }
    builder.Services.AddSingleton(repositorioEmMemoria);
    builder.Services.AddSingleton<IRepositorioEspacoTrabalho>(repositorioEmMemoria);
    builder.Services.AddSingleton<IRepositorioDecisao>(repositorioEmMemoria);
    builder.Services.AddSingleton<IRepositorioIncidente>(repositorioEmMemoria);
    builder.Services.AddSingleton<IRepositorioContexto>(repositorioEmMemoria);
    builder.Services.AddSingleton<IRepositorioRecibo>(repositorioEmMemoria);
    builder.Services.AddSingleton<IReconstrutorProjecoes>(repositorioEmMemoria);
}

builder.Services.AddSingleton<IServicoIdentidade, ServicoIdentidade>();
builder.Services.AddSingleton<McpDispatcher>();
builder.Services.AddSingleton<McpEndpointHandler>();
builder.Services.AddSingleton<WebReviewHandler>();

var app = builder.Build();

app.UseStaticFiles();

if (args.Contains("--migrate") && !string.IsNullOrEmpty(stringConexao))
{
    var migrador = new MigradorBancoDados(stringConexao);
    var caminhoMigracoes = Path.Combine(AppContext.BaseDirectory, "../../../db/migrations");
    if (!Directory.Exists(caminhoMigracoes))
        caminhoMigracoes = Path.Combine(AppContext.BaseDirectory, "db/migrations");
    if (!Directory.Exists(caminhoMigracoes))
        caminhoMigracoes = Path.Combine(Directory.GetCurrentDirectory(), "db/migrations");
    await migrador.MigrarAsync(caminhoMigracoes);
    Console.WriteLine("Migrações aplicadas com sucesso.");
    return;
}

if (string.Equals(provedorArmazenamento, "PostgreSQL", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(stringConexao))
{
    var autoMigrar = builder.Configuration.GetValue<bool>("Sentinel:AutoMigrate", true);
    if (autoMigrar)
    {
        var migrador = new MigradorBancoDados(stringConexao);
        var caminhoMigracoes = Path.Combine(AppContext.BaseDirectory, "../../../db/migrations");
        if (!Directory.Exists(caminhoMigracoes))
            caminhoMigracoes = Path.Combine(AppContext.BaseDirectory, "db/migrations");
        if (!Directory.Exists(caminhoMigracoes))
            caminhoMigracoes = Path.Combine(Directory.GetCurrentDirectory(), "db/migrations");
        if (Directory.Exists(caminhoMigracoes))
        {
            await migrador.MigrarAsync(caminhoMigracoes);
        }
    }
}

if (args.Contains("--rebuild-projections"))
{
    var reconstrutor = app.Services.GetRequiredService<IReconstrutorProjecoes>();
    await reconstrutor.ReconstruirProjecoesAsync();
    Console.WriteLine("Projeções reconstruídas com sucesso.");
    return;
}

app.MapGet("/healthz", () => Results.Ok(new { status = "Healthy", timestamp = DateTimeOffset.UtcNow }));
app.MapGet("/readyz", () => Results.Ok(new { status = "Ready", version = "0.1.0" }));

app.MapMethods("/mcp", ["GET", "POST"], async (HttpContext ctx, McpEndpointHandler handler) =>
{
    await handler.HandleAsync(ctx);
});

app.MapGet("/", async (HttpContext ctx, WebReviewHandler handler) =>
{
    await handler.RenderLandingPageAsync(ctx);
});

app.MapGet("/review", async (HttpContext ctx, WebReviewHandler handler) =>
{
    await handler.RenderReviewPageAsync(ctx);
});

app.MapGet("/app", async (HttpContext ctx, WebReviewHandler handler) =>
{
    await handler.RenderReviewPageAsync(ctx);
});

app.MapPost("/api/review/approve", async (HttpContext ctx, WebReviewHandler handler) =>
{
    await handler.HandleApproveAsync(ctx);
});

app.MapPost("/api/review/reject", async (HttpContext ctx, WebReviewHandler handler) =>
{
    await handler.HandleRejectAsync(ctx);
});

app.MapPost("/api/review/restore", async (HttpContext ctx, WebReviewHandler handler) =>
{
    await handler.HandleRestoreAsync(ctx);
});

app.Run();

public partial class Program { }
