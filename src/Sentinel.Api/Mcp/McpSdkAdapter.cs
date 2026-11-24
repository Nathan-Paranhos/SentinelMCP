using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Sentinel.Api.Auth;
using Sentinel.Api.Operacao;
using Sentinel.Core.Excecoes;

namespace Sentinel.Api.Mcp;

public sealed class IdentidadeAmbienteStdio
{
    public IdentidadeAgente? Atual { get; set; }
}

// Integra o SDK oficial ModelContextProtocol.AspNetCore com o núcleo Sentinel
public static class McpSdkAdapter
{
    private const string ChaveHttpContext = "SentinelAgentIdentity";

    private static readonly JsonSerializerOptions OpcoesJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public static IMcpServerBuilder ConfigureSentinelMcp(this IMcpServerBuilder builder)
    {
        return builder
            .WithListToolsHandler((ctx, ct) =>
            {
                var tools = McpToolsRegistry.AllTools.Select(t => new Tool
                {
                    Name = t.Name,
                    Title = t.Title,
                    Description = t.Description,
                    InputSchema = JsonSerializer.SerializeToElement(t.InputSchema, OpcoesJson),
                    OutputSchema = t.OutputSchema != null ? JsonSerializer.SerializeToElement(t.OutputSchema, OpcoesJson) : null,
                    Annotations = t.Annotations == null
                        ? null
                        : new ToolAnnotations
                        {
                            Title = t.Annotations.Title,
                            ReadOnlyHint = t.Annotations.ReadOnlyHint,
                            DestructiveHint = t.Annotations.DestructiveHint,
                            IdempotentHint = t.Annotations.IdempotentHint,
                            OpenWorldHint = t.Annotations.OpenWorldHint
                        }
                }).ToList();

                return ValueTask.FromResult(new ListToolsResult { Tools = tools });
            })
            .WithCallToolHandler(async (ctx, ct) =>
            {
                var identidade = ResolverIdentidade(ctx.Services);
                var despachante = ctx.Services!.GetRequiredService<McpDispatcher>();
                var http = ctx.Services!.GetService<IHttpContextAccessor>()?.HttpContext;
                var correlacao = http?.TraceIdentifier ?? Guid.NewGuid().ToString("N")[..12];

                var nome = ctx.Params?.Name;
                if (string.IsNullOrWhiteSpace(nome) || !McpDispatcher.Existe(nome))
                    throw new McpProtocolException($"Ferramenta desconhecida: {nome}", McpErrorCode.InvalidParams);

                JsonElement argsElement = ctx.Params?.Arguments != null
                    ? JsonSerializer.SerializeToElement(ctx.Params.Arguments, OpcoesJson)
                    : default;

                var resultado = await despachante.CallToolAsync(nome, argsElement, identidade, correlacao, ct);
                return new CallToolResult
                {
                    IsError = resultado.IsError,
                    StructuredContent = resultado.StructuredContent != null
                        ? JsonSerializer.SerializeToElement(resultado.StructuredContent, OpcoesJson)
                        : null,
                    Content = resultado.Content.Select(c => (ContentBlock)new TextContentBlock { Text = c.Text }).ToList()
                };
            })
            .WithListResourcesHandler((ctx, ct) =>
            {
                var resources = McpDispatcher.AllResources.Select(r => new Resource
                {
                    Uri = r.Uri,
                    Name = r.Name,
                    Title = r.Title,
                    Description = r.Description,
                    MimeType = r.MimeType
                }).ToList();

                return ValueTask.FromResult(new ListResourcesResult { Resources = resources });
            })
            .WithListResourceTemplatesHandler((ctx, ct) =>
            {
                var templates = McpDispatcher.AllResourceTemplates.Select(t => new ResourceTemplate
                {
                    UriTemplate = t.UriTemplate,
                    Name = t.Name,
                    Title = t.Title,
                    Description = t.Description,
                    MimeType = t.MimeType
                }).ToList();

                return ValueTask.FromResult(new ListResourceTemplatesResult { ResourceTemplates = templates });
            })
            .WithReadResourceHandler(async (ctx, ct) =>
            {
                var uri = ctx.Params?.Uri;
                if (string.IsNullOrWhiteSpace(uri))
                    throw new McpProtocolException("Parâmetro 'uri' obrigatório.", McpErrorCode.InvalidParams);

                var identidade = ResolverIdentidade(ctx.Services);
                var despachante = ctx.Services!.GetRequiredService<McpDispatcher>();
                try
                {
                    var (mime, texto) = await despachante.ReadResourceAsync(uri, identidade, ct);
                    return new ReadResourceResult
                    {
                        Contents = [new TextResourceContents { Uri = uri, MimeType = mime, Text = texto }]
                    };
                }
                catch (ExcecaoDeDominio ex)
                {
                    throw new McpProtocolException(ex.Message, McpErrorCode.InvalidParams);
                }
            })
            .WithListPromptsHandler((ctx, ct) =>
            {
                var prompts = McpDispatcher.AllPrompts.Select(p => new Prompt
                {
                    Name = p.Name,
                    Title = p.Title,
                    Description = p.Description,
                    Arguments = p.Arguments.Select(a => new PromptArgument
                    {
                        Name = a.Name,
                        Description = a.Description,
                        Required = a.Required
                    }).ToList()
                }).ToList();

                return ValueTask.FromResult(new ListPromptsResult { Prompts = prompts });
            })
            .WithGetPromptHandler((ctx, ct) =>
            {
                var nome = ctx.Params?.Name;
                if (string.IsNullOrWhiteSpace(nome))
                    throw new McpProtocolException("Parâmetro 'name' obrigatório.", McpErrorCode.InvalidParams);

                Dictionary<string, string?>? args = null;
                if (ctx.Params?.Arguments != null)
                {
                    args = ctx.Params.Arguments.ToDictionary(
                        kv => kv.Key,
                        kv => kv.Value.ValueKind == JsonValueKind.String ? kv.Value.GetString() : kv.Value.ToString(),
                        StringComparer.Ordinal);
                }

                try
                {
                    var (desc, texto) = McpDispatcher.GetPromptContent(nome, args);
                    return ValueTask.FromResult(new GetPromptResult
                    {
                        Description = desc,
                        Messages = [new PromptMessage { Role = Role.User, Content = new TextContentBlock { Text = texto } }]
                    });
                }
                catch (ExcecaoDeNaoEncontrado ex)
                {
                    throw new McpProtocolException(ex.Message, McpErrorCode.InvalidParams);
                }
            });
    }

    public static void ConfigureMcpServerOptions(McpServerOptions options)
    {
        options.ServerInfo = new Implementation
        {
            Name = SentinelDiagnostics.ServiceName,
            Version = SentinelDiagnostics.ServiceVersion,
            Title = "Sentinel-MCP"
        };
        options.ServerInstructions = McpEndpointHandler.InstrucoesAgente;
    }

    // Autentica requisições ao endpoint /mcp/sse antes do middleware do SDK
    public static async Task AutenticarHttpAsync(HttpContext ctx, RequestDelegate next)
    {
        var handler = ctx.RequestServices.GetRequiredService<McpEndpointHandler>();
        if (!handler.OrigemPermitida(ctx))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsJsonAsync(new { code = "FORBIDDEN", message = "Origin não permitida." }, OpcoesJson, ctx.RequestAborted);
            return;
        }

        var servicoIdentidade = ctx.RequestServices.GetRequiredService<IServicoIdentidadeAgente>();
        try
        {
            var identidade = await servicoIdentidade.AutenticarAsync(ctx);
            ctx.Items[ChaveHttpContext] = identidade;
            await next(ctx);
        }
        catch (ExcecaoDeNaoAutorizado ex)
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            ctx.Response.Headers.WWWAuthenticate = handler.CabecalhoDesafio(ctx);
            await ctx.Response.WriteAsJsonAsync(new { code = "UNAUTHORIZED", message = ex.Message }, OpcoesJson, ctx.RequestAborted);
        }
    }

    private static IdentidadeAgente ResolverIdentidade(IServiceProvider? services)
    {
        var http = services?.GetService<IHttpContextAccessor>()?.HttpContext;
        if (http != null && http.Items.TryGetValue(ChaveHttpContext, out var obj) && obj is IdentidadeAgente idHttp)
            return idHttp;

        var stdio = services?.GetService<IdentidadeAmbienteStdio>()?.Atual;
        if (stdio != null)
            return stdio;

        throw new McpProtocolException("Credencial MCP ausente.", McpErrorCode.InvalidRequest);
    }
}
