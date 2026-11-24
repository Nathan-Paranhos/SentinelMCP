using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sentinel.Api.Auth;
using Sentinel.Core.CasosDeUso;
using Sentinel.Core.Excecoes;
using Sentinel.Core.Modelos;

namespace Sentinel.Api.Admin;

public sealed class AdminEndpointHandler
{
    private static readonly JsonSerializerOptions OpcoesJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly ServicoDeMemoria _servico;
    private readonly IConfiguration _config;
    private readonly IHostEnvironment _ambiente;
    private readonly ILogger<AdminEndpointHandler> _log;

    public AdminEndpointHandler(
        ServicoDeMemoria servico,
        IConfiguration config,
        IHostEnvironment ambiente,
        ILogger<AdminEndpointHandler> log)
    {
        _servico = servico;
        _config = config;
        _ambiente = ambiente;
        _log = log;
    }

    public bool Autenticado(HttpContext ctx, out Ator ator)
    {
        // Autenticação por chave de API
        var chaveConfigurada = _config["Sentinel:AdminApiKey"] ?? Environment.GetEnvironmentVariable("ADMIN_API_KEY");
        if (string.IsNullOrWhiteSpace(chaveConfigurada) && !_ambiente.IsProduction())
            chaveConfigurada = "sentinel-admin-dev-key";

        if (!string.IsNullOrWhiteSpace(chaveConfigurada))
        {
            var headerKey = ctx.Request.Headers["X-Admin-Key"].ToString();
            if (!string.IsNullOrEmpty(headerKey) && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(headerKey), Encoding.UTF8.GetBytes(chaveConfigurada)))
            {
                ator = new Ator("https://auth.sentinel.dev", "admin", "admin-key", CanalDeAcesso.Web);
                return true;
            }

            var auth = ctx.Request.Headers.Authorization.ToString();
            if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                var token = auth["Bearer ".Length..].Trim();
                if (CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(chaveConfigurada)))
                {
                    ator = new Ator("https://auth.sentinel.dev", "admin", "admin-bearer", CanalDeAcesso.Web);
                    return true;
                }
            }
        }

        // Autenticação por sessão web
        if (ctx.User.Identity?.IsAuthenticated == true)
        {
            var iss = ctx.User.FindFirst("iss")?.Value ?? "https://auth.sentinel.dev";
            var sub = ctx.User.FindFirst("sub")?.Value ?? ctx.User.Identity.Name ?? "web-user";
            ator = new Ator(iss, sub, "web-session", CanalDeAcesso.Web);
            return true;
        }

        ator = new Ator("https://auth.sentinel.dev", "anonimo", null, CanalDeAcesso.Web);
        return false;
    }

    private static IResult RespostaErro(ExcecaoDeDominio ex)
    {
        var status = ex switch
        {
            ExcecaoDeNaoAutorizado => StatusCodes.Status401Unauthorized,
            ExcecaoDeAcessoNegado => StatusCodes.Status403Forbidden,
            ExcecaoDeNaoEncontrado => StatusCodes.Status404NotFound,
            ExcecaoDeConflito => StatusCodes.Status409Conflict,
            ExcecaoDeValidacao => StatusCodes.Status400BadRequest,
            _ => StatusCodes.Status400BadRequest
        };
        return Results.Json(new { code = ex.Codigo, message = ex.Message }, statusCode: status);
    }

    public async Task<IResult> HandleListWorkspacesAsync(HttpContext ctx)
    {
        if (!Autenticado(ctx, out var ator))
            return Results.Json(new { code = "UNAUTHORIZED", message = "Credencial administrativa ausente ou inválida." }, statusCode: StatusCodes.Status401Unauthorized);

        var lista = await _servico.ListarTodosEspacosAsync(ator, ctx.RequestAborted);
        return Results.Ok(lista.Select(w => new
        {
            workspaceId = w.EspacoTrabalhoId,
            name = w.Nome,
            slug = w.IdentificadorSlug,
            createdAt = w.CriadoEm
        }));
    }

    public sealed record RequisicaoCriarWorkspace(string? Name, string? Slug, Guid? WorkspaceId);

    public async Task<IResult> HandleCreateWorkspaceAsync(HttpContext ctx)
    {
        if (!Autenticado(ctx, out var ator))
            return Results.Json(new { code = "UNAUTHORIZED", message = "Credencial administrativa ausente ou inválida." }, statusCode: StatusCodes.Status401Unauthorized);

        try
        {
            var req = await JsonSerializer.DeserializeAsync<RequisicaoCriarWorkspace>(ctx.Request.Body, OpcoesJson, ctx.RequestAborted);
            if (req == null)
                return Results.BadRequest(new { code = "VALIDATION_ERROR", message = "Corpo JSON inválido." });

            var ws = await _servico.CriarEspacoTrabalhoAsync(ator, req.Name ?? string.Empty, req.Slug ?? string.Empty, req.WorkspaceId, ctx.RequestAborted);
            _log.LogInformation("Workspace {WorkspaceId} ({Slug}) criado por {Sujeito}", ws.EspacoTrabalhoId, ws.IdentificadorSlug, ator.Sujeito);
            return Results.Created($"/api/admin/workspaces/{ws.EspacoTrabalhoId}", new
            {
                workspaceId = ws.EspacoTrabalhoId,
                name = ws.Nome,
                slug = ws.IdentificadorSlug,
                createdAt = ws.CriadoEm
            });
        }
        catch (ExcecaoDeDominio ex)
        {
            return RespostaErro(ex);
        }
    }

    public async Task<IResult> HandleGetWorkspaceAsync(HttpContext ctx, Guid workspaceId)
    {
        if (!Autenticado(ctx, out var ator))
            return Results.Json(new { code = "UNAUTHORIZED", message = "Credencial administrativa ausente ou inválida." }, statusCode: StatusCodes.Status401Unauthorized);

        try
        {
            var ws = await _servico.ObterPainelAsync(ator, workspaceId, ctx.RequestAborted);
            var vinculos = await _servico.ListarVinculosDoEspacoAsync(ator, workspaceId, ctx.RequestAborted);
            return Results.Ok(new
            {
                workspace = new
                {
                    workspaceId = ws.EspacoTrabalho.EspacoTrabalhoId,
                    name = ws.EspacoTrabalho.Nome,
                    slug = ws.EspacoTrabalho.IdentificadorSlug,
                    createdAt = ws.EspacoTrabalho.CriadoEm
                },
                memberships = vinculos.Select(v => new
                {
                    membershipId = v.VinculoId,
                    workspaceId = v.EspacoTrabalhoId,
                    issuer = v.Emissor,
                    subject = v.Sujeito,
                    role = v.Papel.ParaTexto(),
                    isActive = v.Ativo,
                    createdAt = v.CriadoEm
                })
            });
        }
        catch (ExcecaoDeDominio ex)
        {
            return RespostaErro(ex);
        }
    }

    public sealed record RequisicaoCriarProjeto(string? Name, string? Slug, Guid? ProjectId);

    public async Task<IResult> HandleCreateProjectAsync(HttpContext ctx, Guid workspaceId)
    {
        if (!Autenticado(ctx, out var ator))
            return Results.Json(new { code = "UNAUTHORIZED", message = "Credencial administrativa ausente ou inválida." }, statusCode: StatusCodes.Status401Unauthorized);

        try
        {
            var req = await JsonSerializer.DeserializeAsync<RequisicaoCriarProjeto>(ctx.Request.Body, OpcoesJson, ctx.RequestAborted);
            if (req == null)
                return Results.BadRequest(new { code = "VALIDATION_ERROR", message = "Corpo JSON inválido." });

            var proj = await _servico.CriarProjetoAsync(ator, workspaceId, req.Name ?? string.Empty, req.Slug ?? string.Empty, req.ProjectId, ctx.RequestAborted);
            _log.LogInformation("Projeto {ProjetoId} ({Slug}) criado no workspace {WorkspaceId}", proj.ProjetoId, proj.IdentificadorSlug, workspaceId);
            return Results.Created($"/api/admin/workspaces/{workspaceId}/projects/{proj.ProjetoId}", new
            {
                projectId = proj.ProjetoId,
                workspaceId = proj.EspacoTrabalhoId,
                name = proj.Nome,
                slug = proj.IdentificadorSlug,
                createdAt = proj.CriadoEm
            });
        }
        catch (ExcecaoDeDominio ex)
        {
            return RespostaErro(ex);
        }
    }

    public sealed record RequisicaoConfigurarVinculo(string? Issuer, string? Subject, string? Role, bool? IsActive);

    public async Task<IResult> HandleConfigureMembershipAsync(HttpContext ctx, Guid workspaceId)
    {
        if (!Autenticado(ctx, out var ator))
            return Results.Json(new { code = "UNAUTHORIZED", message = "Credencial administrativa ausente ou inválida." }, statusCode: StatusCodes.Status401Unauthorized);

        try
        {
            var req = await JsonSerializer.DeserializeAsync<RequisicaoConfigurarVinculo>(ctx.Request.Body, OpcoesJson, ctx.RequestAborted);
            if (req == null)
                return Results.BadRequest(new { code = "VALIDATION_ERROR", message = "Corpo JSON inválido." });

            var papel = ConversorDeEnumeradores.ParaPapel(req.Role ?? "Reader");
            var vinculo = await _servico.ConfigurarVinculoAsync(ator, workspaceId, req.Issuer ?? string.Empty, req.Subject ?? string.Empty, papel, req.IsActive ?? true, ctx.RequestAborted);
            _log.LogInformation("Vínculo configurado: {Subject} ({Papel}) no workspace {WorkspaceId}", vinculo.Sujeito, vinculo.Papel, workspaceId);
            return Results.Ok(new
            {
                membershipId = vinculo.VinculoId,
                workspaceId = vinculo.EspacoTrabalhoId,
                issuer = vinculo.Emissor,
                subject = vinculo.Sujeito,
                role = vinculo.Papel.ParaTexto(),
                isActive = vinculo.Ativo,
                createdAt = vinculo.CriadoEm
            });
        }
        catch (ExcecaoDeDominio ex)
        {
            return RespostaErro(ex);
        }
    }

    public async Task<IResult> HandleListMembershipsAsync(HttpContext ctx, Guid workspaceId)
    {
        if (!Autenticado(ctx, out var ator))
            return Results.Json(new { code = "UNAUTHORIZED", message = "Credencial administrativa ausente ou inválida." }, statusCode: StatusCodes.Status401Unauthorized);

        try
        {
            var lista = await _servico.ListarVinculosDoEspacoAsync(ator, workspaceId, ctx.RequestAborted);
            return Results.Ok(lista.Select(v => new
            {
                membershipId = v.VinculoId,
                workspaceId = v.EspacoTrabalhoId,
                issuer = v.Emissor,
                subject = v.Sujeito,
                role = v.Papel.ParaTexto(),
                isActive = v.Ativo,
                createdAt = v.CriadoEm
            }));
        }
        catch (ExcecaoDeDominio ex)
        {
            return RespostaErro(ex);
        }
    }

    public async Task<IResult> HandleRemoveMembershipAsync(HttpContext ctx, Guid workspaceId, Guid membershipId)
    {
        if (!Autenticado(ctx, out var ator))
            return Results.Json(new { code = "UNAUTHORIZED", message = "Credencial administrativa ausente ou inválida." }, statusCode: StatusCodes.Status401Unauthorized);

        try
        {
            var removido = await _servico.RemoverVinculoAsync(ator, workspaceId, membershipId, ctx.RequestAborted);
            if (!removido)
                return Results.NotFound(new { code = "NOT_FOUND", message = $"Vínculo {membershipId} não encontrado no workspace." });

            _log.LogInformation("Vínculo {MembershipId} removido do workspace {WorkspaceId}", membershipId, workspaceId);
            return Results.Ok(new { success = true, removedMembershipId = membershipId });
        }
        catch (ExcecaoDeDominio ex)
        {
            return RespostaErro(ex);
        }
    }
}
