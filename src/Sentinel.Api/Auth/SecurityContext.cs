using Sentinel.Core.Exceptions;
using Sentinel.Core.Models;
using Sentinel.Persistence.Contracts;

namespace Sentinel.Api.Auth;

public sealed record ResolvedIdentity(
    string Issuer,
    string Subject,
    string? ClientId,
    List<string> Scopes,
    UserRole? DeclaredRole
);

public interface IIdentityService
{
    Task<Membership> AuthenticateAndAuthorizeAsync(
        HttpContext httpContext,
        Guid workspaceId,
        string requiredScope,
        UserRole minRequiredRole,
        CancellationToken ct = default);

    ResolvedIdentity ResolveIdentity(HttpContext httpContext);
}

public sealed class IdentityService : IIdentityService
{
    private readonly IWorkspaceRepository _workspaceRepo;

    public IdentityService(IWorkspaceRepository workspaceRepo)
    {
        _workspaceRepo = workspaceRepo;
    }

    public ResolvedIdentity ResolveIdentity(HttpContext httpContext)
    {
        var authHeader = httpContext.Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedDomainException("Missing or invalid Authorization header. Expected Bearer token.");
        }

        var token = authHeader["Bearer ".Length..].Trim();

        // Support test/mock token format:
        // 1) mock:<subject>:<role>:<scopes_comma_separated>:<client_id>
        // 2) mock:https://auth.sentinel.dev:<subject>:<role>:<scopes>:<client_id>
        if (token.StartsWith("mock:", StringComparison.OrdinalIgnoreCase))
        {
            var raw = token["mock:".Length..].Trim();
            string issuer = "https://auth.sentinel.dev";
            string subject = "agent-dev-1";
            string roleStr = "Contributor";
            string scopesStr = "read:memory,write:decision,write:incident";
            string clientId = "claude-code";

            if (raw.Contains('|'))
            {
                var parts = raw.Split('|');
                if (parts.Length > 0 && !string.IsNullOrEmpty(parts[0])) subject = parts[0];
                if (parts.Length > 1 && !string.IsNullOrEmpty(parts[1])) roleStr = parts[1];
                if (parts.Length > 2 && !string.IsNullOrEmpty(parts[2])) scopesStr = parts[2];
                if (parts.Length > 3 && !string.IsNullOrEmpty(parts[3])) clientId = parts[3];
                if (parts.Length > 4 && !string.IsNullOrEmpty(parts[4])) issuer = parts[4];
            }
            else
            {
                var schemeEnd = raw.IndexOf("://", StringComparison.OrdinalIgnoreCase);
                if (schemeEnd >= 0)
                {
                    var nextColon = raw.IndexOf(':', schemeEnd + 3);
                    if (nextColon >= 0)
                    {
                        issuer = raw[..nextColon];
                        raw = raw[(nextColon + 1)..];
                    }
                }

                var parts = raw.Split(':');
                if (parts.Length == 1 && !string.IsNullOrEmpty(parts[0]))
                {
                    subject = parts[0];
                }
                else if (parts.Length >= 2)
                {
                    subject = parts[0];
                    roleStr = parts[1];
                    var remainder = string.Join(':', parts.Skip(2));
                    var lastColon = remainder.LastIndexOf(':');
                    if (lastColon >= 0 && (remainder.Contains("read:") || remainder.Contains("write:")))
                    {
                        scopesStr = remainder[..lastColon];
                        clientId = remainder[(lastColon + 1)..];
                    }
                    else
                    {
                        scopesStr = remainder;
                    }
                }
            }

            Enum.TryParse<UserRole>(roleStr, true, out var role);
            var scopes = scopesStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

            return new ResolvedIdentity(issuer, subject, clientId, scopes, role);
        }

        // For demo/dev default: fallback to agent-dev-1 with all scopes if simple token passed
        return new ResolvedIdentity(
            Issuer: "https://auth.sentinel.dev",
            Subject: token,
            ClientId: "claude-code",
            Scopes: ["read:memory", "write:decision", "write:incident"],
            DeclaredRole: UserRole.Contributor
        );
    }

    public async Task<Membership> AuthenticateAndAuthorizeAsync(
        HttpContext httpContext,
        Guid workspaceId,
        string requiredScope,
        UserRole minRequiredRole,
        CancellationToken ct = default)
    {
        var identity = ResolveIdentity(httpContext);

        // 1. Validate Scope
        if (!identity.Scopes.Contains(requiredScope, StringComparer.OrdinalIgnoreCase))
        {
            throw new UnauthorizedDomainException($"Token lacks required scope '{requiredScope}'. Available: {string.Join(", ", identity.Scopes)}");
        }

        // 2. Validate Membership in workspace
        var membership = await _workspaceRepo.GetMembershipAsync(workspaceId, identity.Issuer, identity.Subject, ct);
        if (membership == null || !membership.IsActive)
        {
            throw new UnauthorizedDomainException($"No active membership found for user '{identity.Subject}' in workspace '{workspaceId}'.");
        }

        // 3. Validate Role hierarchy: Reader (0) <= Contributor (1) <= Maintainer (2)
        if (membership.Role < minRequiredRole)
        {
            throw new UnauthorizedDomainException($"Insufficient role. Required: {minRequiredRole}, actual: {membership.Role}");
        }

        return membership;
    }
}
