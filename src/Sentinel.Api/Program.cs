using Sentinel.Api.Auth;
using Sentinel.Api.Mcp;
using Sentinel.Api.WebReview;
using Sentinel.Persistence.Contracts;
using Sentinel.Persistence.InMemory;
using Sentinel.Persistence.PostgreSql;

var builder = WebApplication.CreateBuilder(args);

// Configure Storage Provider
var connectionString = builder.Configuration.GetConnectionString("Database");
var storageProvider = builder.Configuration.GetValue<string>("Sentinel:StorageProvider") ?? "InMemory";

if (string.Equals(storageProvider, "PostgreSQL", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(connectionString))
{
    var pgRepo = new PostgreSqlSentinelRepository(connectionString);
    builder.Services.AddSingleton<IWorkspaceRepository>(pgRepo);
    builder.Services.AddSingleton<IDecisionRepository>(pgRepo);
    builder.Services.AddSingleton<IIncidentRepository>(pgRepo);
    builder.Services.AddSingleton<IContextRepository>(pgRepo);
    builder.Services.AddSingleton<IReceiptRepository>(pgRepo);
    builder.Services.AddSingleton<IProjectionRebuilder>(pgRepo);
}
else
{
    var inMemoryRepo = new InMemorySentinelRepository();
    inMemoryRepo.SeedDemo();
    builder.Services.AddSingleton(inMemoryRepo);
    builder.Services.AddSingleton<IWorkspaceRepository>(inMemoryRepo);
    builder.Services.AddSingleton<IDecisionRepository>(inMemoryRepo);
    builder.Services.AddSingleton<IIncidentRepository>(inMemoryRepo);
    builder.Services.AddSingleton<IContextRepository>(inMemoryRepo);
    builder.Services.AddSingleton<IReceiptRepository>(inMemoryRepo);
    builder.Services.AddSingleton<IProjectionRebuilder>(inMemoryRepo);
}

// Register Application Services
builder.Services.AddSingleton<IIdentityService, IdentityService>();
builder.Services.AddSingleton<McpDispatcher>();
builder.Services.AddSingleton<McpEndpointHandler>();
builder.Services.AddSingleton<WebReviewHandler>();

var app = builder.Build();

// Handle CLI subcommands if passed
if (args.Contains("--migrate") && !string.IsNullOrEmpty(connectionString))
{
    var migrator = new DatabaseMigrator(connectionString);
    var migrationsPath = Path.Combine(AppContext.BaseDirectory, "../../../db/migrations");
    if (!Directory.Exists(migrationsPath))
        migrationsPath = Path.Combine(Directory.GetCurrentDirectory(), "db/migrations");
    await migrator.MigrateAsync(migrationsPath);
    Console.WriteLine("Migrations applied successfully.");
    return;
}

if (args.Contains("--rebuild-projections"))
{
    var rebuilder = app.Services.GetRequiredService<IProjectionRebuilder>();
    await rebuilder.RebuildProjectionsAsync();
    Console.WriteLine("Projections rebuilt successfully.");
    return;
}

// Health Checks
app.MapGet("/healthz", () => Results.Ok(new { status = "Healthy", timestamp = DateTimeOffset.UtcNow }));
app.MapGet("/readyz", () => Results.Ok(new { status = "Ready", version = "0.1.0" }));

// MCP Streamable HTTP Endpoint
app.MapMethods("/mcp", ["GET", "POST"], async (HttpContext ctx, McpEndpointHandler handler) =>
{
    await handler.HandleAsync(ctx);
});

// Web Review Routes
app.MapGet("/review", async (HttpContext ctx, WebReviewHandler handler) =>
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

// Expose Program class for WebApplicationFactory in integration tests
public partial class Program { }
