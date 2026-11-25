using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
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
    private readonly IAntiforgery _antiforgery;
    private readonly IConfiguration _config;
    private readonly IHostEnvironment _ambiente;
    private readonly ILogger<AdminEndpointHandler> _log;

    public AdminEndpointHandler(
        ServicoDeMemoria servico,
        IAntiforgery antiforgery,
        IConfiguration config,
        IHostEnvironment ambiente,
        ILogger<AdminEndpointHandler> log)
    {
        _servico = servico;
        _antiforgery = antiforgery;
        _config = config;
        _ambiente = ambiente;
        _log = log;
    }

    public async Task<Ator?> AutenticarAsync(HttpContext ctx, bool exigirCsrfSeCookie = false)
    {
        // Autenticação por chave de API administrativa
        var chaveConfigurada = _config["Sentinel:AdminApiKey"] ?? Environment.GetEnvironmentVariable("ADMIN_API_KEY");
        if (string.IsNullOrWhiteSpace(chaveConfigurada) && !_ambiente.IsProduction())
            chaveConfigurada = "sentinel-admin-dev-key";

        if (!string.IsNullOrWhiteSpace(chaveConfigurada))
        {
            var bytesChave = Encoding.UTF8.GetBytes(chaveConfigurada);
            var headerKey = ctx.Request.Headers["X-Admin-Key"].ToString();
            if (!string.IsNullOrEmpty(headerKey) && ChaveIgual(headerKey, bytesChave))
                return new Ator("https://auth.sentinel.dev", "admin", "admin-key", CanalDeAcesso.AdminSistema);

            var auth = ctx.Request.Headers.Authorization.ToString();
            if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                var token = auth["Bearer ".Length..].Trim();
                if (!string.IsNullOrEmpty(token) && ChaveIgual(token, bytesChave))
                    return new Ator("https://auth.sentinel.dev", "admin", "admin-bearer", CanalDeAcesso.AdminSistema);
            }
        }

        // Bloqueia tokens Bearer comuns em rotas administrativas web
        if (ctx.Request.Headers.Authorization.Count > 0 || ctx.Request.Headers.ContainsKey("X-Admin-Key"))
            return null;

        // Autenticação por cookie da sessão web
        var resultado = await ctx.AuthenticateAsync(EsquemasDeAutenticacao.CookieWeb);
        if (!resultado.Succeeded || resultado.Principal == null)
            return null;

        var iss = resultado.Principal.FindFirst("iss")?.Value;
        var sub = resultado.Principal.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(iss) || string.IsNullOrWhiteSpace(sub))
            return null;

        if (exigirCsrfSeCookie)
            await _antiforgery.ValidateRequestAsync(ctx);

        ctx.User = resultado.Principal;
        return new Ator(iss, sub, "web-session", CanalDeAcesso.Web);
    }

    private static bool ChaveIgual(string recebida, byte[] configurada)
    {
        var bytesRecebida = Encoding.UTF8.GetBytes(recebida);
        return bytesRecebida.Length == configurada.Length
               && CryptographicOperations.FixedTimeEquals(bytesRecebida, configurada);
    }

    private static IResult NaoAutorizado() =>
        Results.Json(new { code = "UNAUTHORIZED", message = "Credencial administrativa ausente ou inválida." }, statusCode: StatusCodes.Status401Unauthorized);

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

    private async Task<IResult> ExecutarAsync(HttpContext ctx, bool mutacao, Func<Ator, Task<IResult>> acao)
    {
        Ator? ator;
        try
        {
            ator = await AutenticarAsync(ctx, exigirCsrfSeCookie: mutacao);
        }
        catch (AntiforgeryValidationException ex)
        {
            _log.LogWarning("Validação CSRF administrativa falhou: {Motivo}", ex.Message);
            return Results.Json(new { code = "CSRF_INVALID", message = "Token CSRF ausente ou inválido." }, statusCode: StatusCodes.Status400BadRequest);
        }

        if (ator == null)
            return NaoAutorizado();

        try
        {
            return await acao(ator);
        }
        catch (ExcecaoDeDominio ex)
        {
            return RespostaErro(ex);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { code = "VALIDATION_ERROR", message = "Corpo JSON inválido." });
        }
    }

    public Task<IResult> HandleListWorkspacesAsync(HttpContext ctx) => ExecutarAsync(ctx, mutacao: false, async ator =>
    {
        var lista = await _servico.ListarTodosEspacosAsync(ator, ctx.RequestAborted);
        return Results.Ok(lista.Select(w => new
        {
            workspaceId = w.EspacoTrabalhoId,
            name = w.Nome,
            slug = w.IdentificadorSlug,
            createdAt = w.CriadoEm
        }));
    });

    public sealed record RequisicaoCriarWorkspace(string? Name, string? Slug, Guid? WorkspaceId, string? OwnerIssuer = null, string? OwnerSubject = null, string? OwnerRole = null);

    public Task<IResult> HandleCreateWorkspaceAsync(HttpContext ctx) => ExecutarAsync(ctx, mutacao: true, async ator =>
    {
        var req = await JsonSerializer.DeserializeAsync<RequisicaoCriarWorkspace>(ctx.Request.Body, OpcoesJson, ctx.RequestAborted);
        if (req == null)
            return Results.BadRequest(new { code = "VALIDATION_ERROR", message = "Corpo JSON inválido." });

        var ws = await _servico.CriarEspacoTrabalhoAsync(ator, req.Name ?? string.Empty, req.Slug ?? string.Empty, req.WorkspaceId, ctx.RequestAborted);
        if (!string.IsNullOrWhiteSpace(req.OwnerSubject))
        {
            if (!ConversorDeEnumeradores.TentarConverterPapel(req.OwnerRole ?? "Maintainer", out var papelDono))
                return Results.BadRequest(new { code = "VALIDATION_ERROR", message = "Papel do proprietário inválido." });

            var emissorDono = string.IsNullOrWhiteSpace(req.OwnerIssuer)
                ? (_config["Sentinel:Web:LocalIssuer"] ?? "https://auth.sentinel.dev")
                : req.OwnerIssuer.Trim();
            await _servico.ConfigurarVinculoAsync(ator, ws.EspacoTrabalhoId, emissorDono, req.OwnerSubject.Trim(), papelDono, true, ctx.RequestAborted);
        }

        _log.LogInformation("Workspace {WorkspaceId} ({Slug}) criado por {Sujeito}", ws.EspacoTrabalhoId, ws.IdentificadorSlug, ator.Sujeito);
        return Results.Created($"/api/admin/workspaces/{ws.EspacoTrabalhoId}", new
        {
            workspaceId = ws.EspacoTrabalhoId,
            name = ws.Nome,
            slug = ws.IdentificadorSlug,
            createdAt = ws.CriadoEm
        });
    });

    public sealed record RequisicaoAtualizarWorkspace(string? Name);

    public Task<IResult> HandleUpdateWorkspaceAsync(HttpContext ctx, Guid workspaceId) => ExecutarAsync(ctx, mutacao: true, async ator =>
    {
        var req = await JsonSerializer.DeserializeAsync<RequisicaoAtualizarWorkspace>(ctx.Request.Body, OpcoesJson, ctx.RequestAborted);
        if (req == null)
            return Results.BadRequest(new { code = "VALIDATION_ERROR", message = "Corpo JSON inválido." });

        var ws = await _servico.AtualizarEspacoTrabalhoAsync(ator, workspaceId, req.Name ?? string.Empty, ctx.RequestAborted);
        _log.LogInformation("Workspace {WorkspaceId} salvo com nome '{Nome}' por {Sujeito}", ws.EspacoTrabalhoId, ws.Nome, ator.Sujeito);
        return Results.Ok(new
        {
            workspaceId = ws.EspacoTrabalhoId,
            name = ws.Nome,
            slug = ws.IdentificadorSlug,
            createdAt = ws.CriadoEm
        });
    });

    public Task<IResult> HandleGetWorkspaceAsync(HttpContext ctx, Guid workspaceId) => ExecutarAsync(ctx, mutacao: false, async ator =>
    {
        var detalhes = await _servico.ObterDetalhesEspacoAsync(ator, workspaceId, ctx.RequestAborted);
        return Results.Ok(new
        {
            workspace = new
            {
                workspaceId = detalhes.EspacoTrabalho.EspacoTrabalhoId,
                name = detalhes.EspacoTrabalho.Nome,
                slug = detalhes.EspacoTrabalho.IdentificadorSlug,
                createdAt = detalhes.EspacoTrabalho.CriadoEm
            },
            projects = detalhes.Projetos.Select(p => new
            {
                projectId = p.ProjetoId,
                workspaceId = p.EspacoTrabalhoId,
                name = p.Nome,
                slug = p.IdentificadorSlug,
                createdAt = p.CriadoEm
            }),
            memberships = detalhes.Vinculos.Select(v => new
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
    });

    public Task<IResult> HandleListProjectsAsync(HttpContext ctx, Guid workspaceId) => ExecutarAsync(ctx, mutacao: false, async ator =>
    {
        var projetos = await _servico.ListarProjetosDoEspacoAsync(ator, workspaceId, ctx.RequestAborted);
        return Results.Ok(projetos.Select(p => new
        {
            projectId = p.ProjetoId,
            workspaceId = p.EspacoTrabalhoId,
            name = p.Nome,
            slug = p.IdentificadorSlug,
            createdAt = p.CriadoEm
        }));
    });

    public sealed record RequisicaoCriarProjeto(string? Name, string? Slug, Guid? ProjectId);

    public Task<IResult> HandleCreateProjectAsync(HttpContext ctx, Guid workspaceId) => ExecutarAsync(ctx, mutacao: true, async ator =>
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
    });

    public sealed record RequisicaoConfigurarVinculo(string? Issuer, string? Subject, string? Role, bool? IsActive);

    public Task<IResult> HandleConfigureMembershipAsync(HttpContext ctx, Guid workspaceId) => ExecutarAsync(ctx, mutacao: true, async ator =>
    {
        var req = await JsonSerializer.DeserializeAsync<RequisicaoConfigurarVinculo>(ctx.Request.Body, OpcoesJson, ctx.RequestAborted);
        if (req == null)
            return Results.BadRequest(new { code = "VALIDATION_ERROR", message = "Corpo JSON inválido." });

        if (!ConversorDeEnumeradores.TentarConverterPapel(req.Role ?? "Reader", out var papel))
            return Results.BadRequest(new { code = "VALIDATION_ERROR", message = "Papel inválido. Valores aceitos: Reader, Contributor, Maintainer." });

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
    });

    public Task<IResult> HandleListMembershipsAsync(HttpContext ctx, Guid workspaceId) => ExecutarAsync(ctx, mutacao: false, async ator =>
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
    });

    public Task<IResult> HandleRemoveMembershipAsync(HttpContext ctx, Guid workspaceId, Guid membershipId) => ExecutarAsync(ctx, mutacao: true, async ator =>
    {
        var removido = await _servico.RemoverVinculoAsync(ator, workspaceId, membershipId, ctx.RequestAborted);
        if (!removido)
            return Results.NotFound(new { code = "NOT_FOUND", message = $"Vínculo {membershipId} não encontrado no workspace." });

        _log.LogInformation("Vínculo {MembershipId} removido do workspace {WorkspaceId}", membershipId, workspaceId);
        return Results.Ok(new { success = true, removedMembershipId = membershipId });
    });

    public Task<IResult> HandleDeleteProjectAsync(HttpContext ctx, Guid workspaceId, Guid projectId) => ExecutarAsync(ctx, mutacao: true, async ator =>
    {
        var removido = await _servico.RemoverProjetoAsync(ator, workspaceId, projectId, ctx.RequestAborted);
        if (!removido)
            return Results.NotFound(new { code = "NOT_FOUND", message = $"Projeto {projectId} não encontrado no workspace." });

        _log.LogInformation("Projeto {ProjectId} removido do workspace {WorkspaceId}", projectId, workspaceId);
        return Results.Ok(new { success = true, removedProjectId = projectId });
    });

    public Task<IResult> HandleListApiKeysAsync(HttpContext ctx, Guid workspaceId) => ExecutarAsync(ctx, mutacao: false, async ator =>
    {
        var chaves = await _servico.ListarChavesApiAsync(ator, workspaceId, ctx.RequestAborted);
        return Results.Ok(chaves.Select(k => new
        {
            keyId = k.ChaveId,
            workspaceId = k.EspacoTrabalhoId,
            name = k.Nome,
            keyPrefix = k.PrefixoChave,
            issuer = k.Emissor,
            subject = k.Sujeito,
            clientId = k.ClienteId,
            scopes = k.Escopos,
            isActive = k.Ativo,
            createdBySubject = k.CriadoPorSujeito,
            createdAt = k.CriadoEm,
            expiresAt = k.ExpiraEm,
            lastUsedAt = k.UltimoUsoEm
        }));
    });

    public sealed record RequisicaoCriarChaveApi(string? Name, string? Subject, string? ClientId, string? Role, List<string>? Scopes, int? ExpiresInDays);

    public Task<IResult> HandleCreateApiKeyAsync(HttpContext ctx, Guid workspaceId) => ExecutarAsync(ctx, mutacao: true, async ator =>
    {
        var req = await JsonSerializer.DeserializeAsync<RequisicaoCriarChaveApi>(ctx.Request.Body, OpcoesJson, ctx.RequestAborted);
        if (req == null)
            return Results.BadRequest(new { code = "VALIDATION_ERROR", message = "Corpo JSON inválido." });

        if (!ConversorDeEnumeradores.TentarConverterPapel(req.Role ?? "Contributor", out var papel))
            return Results.BadRequest(new { code = "VALIDATION_ERROR", message = "Papel inválido. Valores aceitos: Reader, Contributor, Maintainer." });

        var emissorLocal = _config["Sentinel:Web:LocalIssuer"] ?? "https://auth.sentinel.dev";
        var resultado = await _servico.CriarChaveApiAsync(
            ator,
            new EntradaCriarChaveApi(workspaceId, req.Name, req.Subject, req.ClientId, papel, req.Scopes, req.ExpiresInDays),
            emissorLocal,
            ctx.RequestAborted);

        var k = resultado.Chave;
        _log.LogInformation("Chave de API {KeyPrefix}... criada para {Subject} no workspace {WorkspaceId}", k.PrefixoChave, k.Sujeito, workspaceId);
        return Results.Created($"/api/admin/workspaces/{workspaceId}/api-keys/{k.ChaveId}", new
        {
            key = new
            {
                keyId = k.ChaveId,
                workspaceId = k.EspacoTrabalhoId,
                name = k.Nome,
                keyPrefix = k.PrefixoChave,
                issuer = k.Emissor,
                subject = k.Sujeito,
                clientId = k.ClienteId,
                scopes = k.Escopos,
                isActive = k.Ativo,
                createdBySubject = k.CriadoPorSujeito,
                createdAt = k.CriadoEm,
                expiresAt = k.ExpiraEm,
                lastUsedAt = k.UltimoUsoEm
            },
            token = resultado.TokenPlano
        });
    });

    public Task<IResult> HandleRevokeApiKeyAsync(HttpContext ctx, Guid workspaceId, Guid keyId) => ExecutarAsync(ctx, mutacao: true, async ator =>
    {
        var revogada = await _servico.RevogarChaveApiAsync(ator, workspaceId, keyId, ctx.RequestAborted);
        if (!revogada)
            return Results.NotFound(new { code = "NOT_FOUND", message = $"Chave {keyId} não encontrada no workspace." });

        _log.LogInformation("Chave de API {KeyId} revogada no workspace {WorkspaceId}", keyId, workspaceId);
        return Results.Ok(new { success = true, revokedKeyId = keyId });
    });

    public Task<IResult> HandleListUsersAsync(HttpContext ctx, Guid workspaceId) => ExecutarAsync(ctx, mutacao: false, async ator =>
    {
        var lista = await _servico.ListarUsuariosDoEspacoAsync(ator, workspaceId, ctx.RequestAborted);
        return Results.Ok(lista.Select(u => new
        {
            userId = u.UsuarioId,
            username = u.NomeUsuario,
            displayName = u.NomeExibicao,
            issuer = u.Emissor,
            subject = u.Sujeito,
            isActive = u.Ativo,
            createdAt = u.CriadoEm
        }));
    });

    public sealed record RequisicaoCriarUsuario(string? Username, string? DisplayName, string? Password, string? Role);

    public Task<IResult> HandleCreateUserAsync(HttpContext ctx, Guid workspaceId) => ExecutarAsync(ctx, mutacao: true, async ator =>
    {
        var req = await JsonSerializer.DeserializeAsync<RequisicaoCriarUsuario>(ctx.Request.Body, OpcoesJson, ctx.RequestAborted);
        if (req == null)
            return Results.BadRequest(new { code = "VALIDATION_ERROR", message = "Corpo JSON inválido." });

        if (!ConversorDeEnumeradores.TentarConverterPapel(req.Role ?? "Reader", out var papel))
            return Results.BadRequest(new { code = "VALIDATION_ERROR", message = "Papel inválido. Valores aceitos: Reader, Contributor, Maintainer." });

        var emissorLocal = _config["Sentinel:Web:LocalIssuer"] ?? "https://auth.sentinel.dev";
        var u = await _servico.CriarUsuarioLocalNoEspacoAsync(
            ator,
            new EntradaCriarUsuarioLocal(workspaceId, req.Username, req.DisplayName, req.Password, papel),
            emissorLocal,
            ctx.RequestAborted);

        _log.LogInformation("Usuário web local {Username} criado no workspace {WorkspaceId}", u.NomeUsuario, workspaceId);
        return Results.Created($"/api/admin/workspaces/{workspaceId}/users/{u.UsuarioId}", new
        {
            userId = u.UsuarioId,
            username = u.NomeUsuario,
            displayName = u.NomeExibicao,
            issuer = u.Emissor,
            subject = u.Sujeito,
            isActive = u.Ativo,
            createdAt = u.CriadoEm
        });
    });
}
