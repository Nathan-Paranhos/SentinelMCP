# Política de Segurança — Sentinel-MCP

A segurança e o isolamento de dados são pilares centrais do **Sentinel-MCP**, especialmente por lidar com agentes autônomos de IA sujeitos a *prompt injection* e memórias técnicas organizacionais.

---

## Versões Suportadas

| Versão / Branch | Suporte de Segurança |
| --- | --- |
| `main` (atual) | Ativo |
| Commits anteriores | Sem backport dedicado; recomenda-se atualizar para `main` |

---

## Como Reportar uma Vulnerabilidade

**Não abra uma Issue pública** para falhas de segurança exploráveis (bypass de RLS, escalonamento de privilégio MCP → Web, vazamento de credenciais, XSS/CSRF).

Em vez disso:
1. Utilize a aba **Security → Advisories → Report a vulnerability** no repositório GitHub (`https://github.com/Nathan-Paranhos/SentinelMCP/security/advisories`), ou contate os mantenedores diretamente com descrição, impacto e passos de reprodução.
2. Inclua se possível a configuração utilizada (`InMemory` ou `PostgreSQL`, `Development` ou `Production`).

---

## Modelo de Garantias de Segurança Implementado

| Camada | Mecanismo | Referência |
| --- | --- | --- |
| **Separação de Autoridade (Anti-Autoaprovação)** | Nenhuma tool MCP pode aprovar, rejeitar ou restaurar decisões. `AprovarDecisaoAsync`, `RejeitarDecisaoAsync` e `RestaurarDecisaoAsync` exigem `CanalDeAcesso.Web`, cookie `HttpOnly` + `X-CSRF-Token` e papel `Maintainer`. Qualquer header `Authorization` nas rotas de revisão é rejeitado com `403`. | [`ServicoDeMemoria.cs`](src/Sentinel.Core/CasosDeUso/ServicoDeMemoria.cs), [`WebReviewHandler.cs`](src/Sentinel.Api/WebReview/WebReviewHandler.cs) |
| **Isolamento Multi-Tenant** | Verificação de vínculo ativo em cada caso de uso (`NOT_FOUND` uniforme para evitar enumeração) + `FORCE ROW LEVEL SECURITY` no PostgreSQL com `app.current_workspace_id` configurado por transação e login `sentinel_app_login` sem `BYPASSRLS`. | [`002_rls_policies.sql`](db/migrations/002_rls_policies.sql), [`003_security_search_and_integrity.sql`](db/migrations/003_security_search_and_integrity.sql) |
| **Imutabilidade de Auditoria** | Triggers PostgreSQL bloqueiam `UPDATE`, `DELETE` e `TRUNCATE` em `memory_events` e `memory_payloads`; `ON DELETE RESTRICT` impede exclusão acidental de projetos ou workspaces com histórico. | [`003_security_search_and_integrity.sql`](db/migrations/003_security_search_and_integrity.sql), [`005_api_keys_and_local_users.sql`](db/migrations/005_api_keys_and_local_users.sql) |
| **Credenciais & Criptografia** | Senhas locais com **PBKDF2-SHA256** (210.000 iterações + salt 128 bits, comparação em tempo constante); chaves de API `snt_...` (256 bits CSPRNG) armazenadas apenas em **hash SHA-256**; suporte a JWT Bearer e OIDC PKCE. | [`SegurancaLocal.cs`](src/Sentinel.Core/Comum/SegurancaLocal.cs), [`SecurityContext.cs`](src/Sentinel.Api/Auth/SecurityContext.cs) |
| **Proteção de Onboarding & Produção** | Tokens `mock:` e `dev-login` ficam desligados por padrão e são ignorados em `Production`; `/auth/setup` em `Production` exige `ADMIN_API_KEY` válida (retornando `503` se ausente no servidor). | [`WebReviewHandler.cs`](src/Sentinel.Api/WebReview/WebReviewHandler.cs), [`Program.cs`](src/Sentinel.Api/Program.cs) |
| **Browser & Proxy Reverso** | CSP estrita (`script-src 'self'`, sem scripts inline), `X-Frame-Options: DENY`, `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, HSTS em produção, validação de URLs `https://` sem credenciais/tokens em query params e suporte a `X-Forwarded-*` + chaves DataProtection persistentes. | [`ValidadorDeInvariantes.cs`](src/Sentinel.Core/Validacao/ValidadorDeInvariantes.cs), [`Program.cs`](src/Sentinel.Api/Program.cs) |
