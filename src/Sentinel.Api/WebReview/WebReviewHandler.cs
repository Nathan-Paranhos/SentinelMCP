using System.Security.Cryptography;
using System.Text.Json;
using Sentinel.Core.Commands;
using Sentinel.Core.Exceptions;
using Sentinel.Core.Models;
using Sentinel.Persistence.Contracts;

namespace Sentinel.Api.WebReview;

public sealed class WebReviewHandler
{
    private readonly IDecisionRepository _decisionRepo;
    private readonly IWorkspaceRepository _workspaceRepo;

    public WebReviewHandler(IDecisionRepository decisionRepo, IWorkspaceRepository workspaceRepo)
    {
        _decisionRepo = decisionRepo;
        _workspaceRepo = workspaceRepo;
    }

    private static string GenerateCsrfToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private void ValidateWebSession(HttpContext context, Guid workspaceId, out string reviewerSubject, out string reviewerIssuer)
    {
        // ADR-004 & AC-05: MCP Bearer tokens are strictly forbidden on Web Review routes!
        var authHeader = context.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrWhiteSpace(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedDomainException("MCP Bearer tokens cannot access human web review routes (ADR-004). Web session required.");
        }

        // Web session authentication via cookie or X-Sentinel-Web-Session header
        var webSession = context.Request.Headers["X-Sentinel-Web-Session"].ToString();
        if (string.IsNullOrWhiteSpace(webSession) && context.Request.Cookies.TryGetValue("sentinel_web_session", out var cookieVal))
        {
            webSession = cookieVal;
        }

        if (string.IsNullOrWhiteSpace(webSession))
        {
            throw new UnauthorizedDomainException("Web session authentication required for review operations.");
        }

        // Parse web session format: reviewer_subject:issuer or default maintainer
        var parts = webSession.Split(':');
        reviewerSubject = parts.Length > 0 && !string.IsNullOrEmpty(parts[0]) ? parts[0] : "maintainer-alice";
        reviewerIssuer = parts.Length > 1 && !string.IsNullOrEmpty(parts[1]) ? parts[1] : "https://auth.sentinel.dev";

        // Validate Maintainer role in workspace
        var membership = _workspaceRepo.GetMembershipAsync(workspaceId, reviewerIssuer, reviewerSubject).GetAwaiter().GetResult();
        if (membership == null || !membership.IsActive || membership.Role != UserRole.Maintainer)
        {
            throw new UnauthorizedDomainException($"User '{reviewerSubject}' does not have active Maintainer privileges in workspace '{workspaceId}'.");
        }

        // CSRF Check on POST
        if (HttpMethods.IsPost(context.Request.Method))
        {
            var csrfHeader = context.Request.Headers["X-CSRF-Token"].ToString();
            if (string.IsNullOrWhiteSpace(csrfHeader))
            {
                throw new UnauthorizedDomainException("CSRF protection check failed: missing X-CSRF-Token.");
            }
        }
    }

    public async Task RenderReviewPageAsync(HttpContext context)
    {
        var csrfToken = GenerateCsrfToken();
        context.Response.Cookies.Append("sentinel_csrf_token", csrfToken, new CookieOptions { HttpOnly = false, SameSite = SameSiteMode.Strict });

        var defaultWsId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var proposals = await _decisionRepo.GetPendingProposalsAsync(defaultWsId);

        context.Response.ContentType = "text/html; charset=utf-8";
        var html = $@"<!DOCTYPE html>
<html lang=""pt-BR"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>Sentinel-MCP — Portal de Revisão de Decisões</title>
    <style>
        body {{ font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; background: #0f172a; color: #f8fafc; margin: 0; padding: 2rem; }}
        .container {{ max-width: 1000px; margin: 0 auto; }}
        header {{ border-bottom: 1px solid #334155; padding-bottom: 1rem; margin-bottom: 2rem; display: flex; justify-content: space-between; align-items: center; }}
        h1 {{ margin: 0; font-size: 1.5rem; color: #38bdf8; }}
        .badge {{ background: #0284c7; color: white; padding: 0.25rem 0.5rem; border-radius: 4px; font-size: 0.75rem; text-transform: uppercase; }}
        .card {{ background: #1e293b; border: 1px solid #334155; border-radius: 8px; padding: 1.5rem; margin-bottom: 1.5rem; }}
        .card h2 {{ margin-top: 0; color: #f1f5f9; font-size: 1.25rem; }}
        .meta {{ color: #94a3b8; font-size: 0.875rem; margin-bottom: 1rem; }}
        .summary {{ background: #0f172a; padding: 1rem; border-radius: 6px; margin: 1rem 0; border-left: 4px solid #38bdf8; }}
        .evidence-list {{ list-style: none; padding: 0; }}
        .evidence-list li {{ margin-bottom: 0.5rem; }}
        .evidence-list a {{ color: #38bdf8; text-decoration: none; }}
        .actions {{ display: flex; gap: 1rem; margin-top: 1.5rem; }}
        button {{ padding: 0.6rem 1.2rem; border-radius: 6px; font-weight: 600; cursor: pointer; border: none; font-size: 0.875rem; }}
        .btn-approve {{ background: #10b981; color: white; }}
        .btn-reject {{ background: #ef4444; color: white; }}
        .empty-state {{ text-align: center; padding: 3rem; color: #64748b; }}
    </style>
</head>
<body>
    <div class=""container"">
        <header>
            <div>
                <h1>Sentinel-MCP — Revisão Humana de ADRs</h1>
                <div class=""meta"">Ambiente de Governança e Decisões de Arquitetura</div>
            </div>
            <span class=""badge"">Papel: Maintainer</span>
        </header>

        <main id=""proposals-container"">";

        if (proposals.Count == 0)
        {
            html += @"<div class=""card empty-state"">
                <h3>Nenhuma proposta pendente no momento.</h3>
                <p>Novas propostas submetidas por agentes via tool <code>propose_decision</code> aparecerão aqui para validação humana.</p>
            </div>";
        }
        else
        {
            foreach (var p in proposals)
            {
                var evidenceHtml = string.Join("", p.Evidence.Select(e => $"<li><strong>[{e.Type}]</strong> <a href=\"{e.Url}\" target=\"_blank\">{e.Label}</a></li>"));
                html += $@"
                <div class=""card"" id=""card-{p.TopicId}"">
                    <h2>{p.Title}</h2>
                    <div class=""meta"">
                        Tópico: <code>{p.TopicKey}</code> | Versão Corrente: {p.StreamVersion} | Workspace: <code>{p.WorkspaceId}</code>
                    </div>
                    <div class=""summary"">
                        <strong>Resumo:</strong> {p.Summary}<br><br>
                        <strong>Justificativa:</strong> {p.Rationale ?? "N/A"}
                    </div>
                    <h4>Evidências Fornecidas:</h4>
                    <ul class=""evidence-list"">{evidenceHtml}</ul>
                    <div class=""actions"">
                        <button class=""btn-approve"" onclick=""approveProposal('{p.WorkspaceId}', '{p.ProjectId}', '{p.TopicId}', '{p.PendingProposalId ?? p.ActiveRecordId}', {p.StreamVersion})"">Aprovar Decisão</button>
                        <button class=""btn-reject"" onclick=""rejectProposal('{p.WorkspaceId}', '{p.ProjectId}', '{p.TopicId}', '{p.PendingProposalId ?? p.ActiveRecordId}', {p.StreamVersion})"">Rejeitar Proposta</button>
                    </div>
                </div>";
            }
        }

        html += $@"
        </main>
    </div>
    <script>
        const csrfToken = '{csrfToken}';
        const sessionHeader = 'maintainer-alice:https://auth.sentinel.dev';

        async function approveProposal(wsId, projId, topicId, propId, version) {{
            const idempotencyKey = 'appr_' + Date.now();
            const res = await fetch('/api/review/approve', {{
                method: 'POST',
                headers: {{
                    'Content-Type': 'application/json',
                    'X-CSRF-Token': csrfToken,
                    'X-Sentinel-Web-Session': sessionHeader
                }},
                body: JSON.stringify({{
                    workspaceId: wsId,
                    projectId: projId,
                    topicId: topicId,
                    proposalId: propId,
                    expectedVersion: version,
                    idempotencyKey: idempotencyKey
                }})
            }});
            const data = await res.json();
            if (res.ok) {{
                alert('Decisão aprovada com sucesso! Nova versão ativa: ' + data.streamVersion);
                location.reload();
            }} else {{
                alert('Falha na aprovação: ' + (data.message || JSON.stringify(data)));
            }}
        }}

        async function rejectProposal(wsId, projId, topicId, propId, version) {{
            const reason = prompt('Motivo da rejeição:');
            if (!reason) return;
            const idempotencyKey = 'rej_' + Date.now();
            const res = await fetch('/api/review/reject', {{
                method: 'POST',
                headers: {{
                    'Content-Type': 'application/json',
                    'X-CSRF-Token': csrfToken,
                    'X-Sentinel-Web-Session': sessionHeader
                }},
                body: JSON.stringify({{
                    workspaceId: wsId,
                    projectId: projId,
                    topicId: topicId,
                    proposalId: propId,
                    expectedVersion: version,
                    reason: reason,
                    idempotencyKey: idempotencyKey
                }})
            }});
            const data = await res.json();
            if (res.ok) {{
                alert('Proposta rejeitada. Versão do tópico: ' + data.streamVersion);
                location.reload();
            }} else {{
                alert('Falha na rejeição: ' + (data.message || JSON.stringify(data)));
            }}
        }}
    </script>
</body>
</html>";

        await context.Response.WriteAsync(html);
    }

    public async Task HandleApproveAsync(HttpContext context)
    {
        try
        {
            var body = await JsonSerializer.DeserializeAsync<ApproveDecisionRequest>(context.Request.Body);
            if (body == null)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            ValidateWebSession(context, body.WorkspaceId, out var sub, out var iss);

            var command = new ApproveDecisionCommand(
                WorkspaceId: body.WorkspaceId,
                ProjectId: body.ProjectId,
                TopicId: body.TopicId,
                ProposalId: body.ProposalId,
                ExpectedVersion: body.ExpectedVersion,
                IdempotencyKey: body.IdempotencyKey,
                ReviewerSubject: sub,
                ReviewerIssuer: iss
            );

            var result = await _decisionRepo.ApproveDecisionAsync(command, context.RequestAborted);
            await context.Response.WriteAsJsonAsync(result);
        }
        catch (DomainException ex)
        {
            context.Response.StatusCode = ex is UnauthorizedDomainException ? StatusCodes.Status403Forbidden : StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { code = ex.Code, message = ex.Message });
        }
    }

    public async Task HandleRejectAsync(HttpContext context)
    {
        try
        {
            var body = await JsonSerializer.DeserializeAsync<RejectDecisionRequest>(context.Request.Body);
            if (body == null)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            ValidateWebSession(context, body.WorkspaceId, out var sub, out var iss);

            var command = new RejectDecisionCommand(
                WorkspaceId: body.WorkspaceId,
                ProjectId: body.ProjectId,
                TopicId: body.TopicId,
                ProposalId: body.ProposalId,
                ExpectedVersion: body.ExpectedVersion,
                Reason: body.Reason ?? "Rejeitado pelo mantenedor.",
                IdempotencyKey: body.IdempotencyKey,
                ReviewerSubject: sub,
                ReviewerIssuer: iss
            );

            var result = await _decisionRepo.RejectDecisionAsync(command, context.RequestAborted);
            await context.Response.WriteAsJsonAsync(result);
        }
        catch (DomainException ex)
        {
            context.Response.StatusCode = ex is UnauthorizedDomainException ? StatusCodes.Status403Forbidden : StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { code = ex.Code, message = ex.Message });
        }
    }

    public async Task HandleRestoreAsync(HttpContext context)
    {
        try
        {
            var body = await JsonSerializer.DeserializeAsync<RestoreDecisionRequest>(context.Request.Body);
            if (body == null)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            ValidateWebSession(context, body.WorkspaceId, out var sub, out var iss);

            var command = new RestoreDecisionCommand(
                WorkspaceId: body.WorkspaceId,
                ProjectId: body.ProjectId,
                TopicId: body.TopicId,
                VersionToRestore: body.VersionToRestore,
                ExpectedVersion: body.ExpectedVersion,
                IdempotencyKey: body.IdempotencyKey,
                ReviewerSubject: sub,
                ReviewerIssuer: iss
            );

            var result = await _decisionRepo.RestoreDecisionAsync(command, context.RequestAborted);
            await context.Response.WriteAsJsonAsync(result);
        }
        catch (DomainException ex)
        {
            context.Response.StatusCode = ex is UnauthorizedDomainException ? StatusCodes.Status403Forbidden : StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { code = ex.Code, message = ex.Message });
        }
    }
}

public sealed record ApproveDecisionRequest(
    Guid WorkspaceId,
    Guid ProjectId,
    Guid TopicId,
    Guid ProposalId,
    int ExpectedVersion,
    string IdempotencyKey
);

public sealed record RejectDecisionRequest(
    Guid WorkspaceId,
    Guid ProjectId,
    Guid TopicId,
    Guid ProposalId,
    int ExpectedVersion,
    string? Reason,
    string IdempotencyKey
);

public sealed record RestoreDecisionRequest(
    Guid WorkspaceId,
    Guid ProjectId,
    Guid TopicId,
    int VersionToRestore,
    int ExpectedVersion,
    string IdempotencyKey
);
