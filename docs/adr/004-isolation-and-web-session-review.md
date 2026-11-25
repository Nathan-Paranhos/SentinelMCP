# ADR 004: Isolamento por Workspace e Separação da Sessão Web de Revisão

## Status
Aceito

## Contexto
Agentes de IA acessam ferramentas via endpoint MCP utilizando tokens Bearer emitidos via OAuth com escopos granulares (ex: `read:memory`, `write:decision`). Se um token de agente MCP pudesse aprovar ou substituir diretamente decisões de arquitetura e governança, qualquer agente vulnerável a prompt injection poderia autovalidar decisões prejudiciais ou ignorar diretrizes corporativas.

Além disso, workspaces diferentes pertencem a equipes ou unidades de negócio isoladas, exigindo garantia de que dados de um workspace nunca vazem para outro.

## Decisão
1. **Separação Estrita de Autoridade**:
   - As tools MCP só permitem `propose_decision` (status `proposed`) e `log_incident` (status `reported`), requerendo escopos `write:decision` / `write:incident` e papéis Contributor ou Maintainer.
   - **Nenhuma tool MCP permite aprovação ou rejeição de decisões**.
   - As rotas de revisão (`/review`, `/api/review/*`) exigem autenticação de sessão web humana, proteção contra CSRF e papel de **Maintainer**.
   - Tokens de agente MCP são sumariamente rejeitados nas rotas de revisão web.
2. **Isolamento por Workspace**:
   - Cada operação valida o vínculo ativo do sujeito com o workspace.
   - O PostgreSQL utiliza Row Level Security (RLS) configurado por transação via `app.current_workspace_id`.
   - Se um agente solicitar um identificador de outro workspace, a resposta é uniformemente `NOT_FOUND`, prevenindo inferência ou vazamento de existência.

## Consequências
- Proteção robusta contra ataques de Prompt Injection e autorização indevida.
- Isolamento auditável e seguro entre workspaces e projetos.
- **Gatilho de revisão**: Avaliar introdução de fluxo de aprovação por múltiplas pessoas (four-eyes principle) para ambientes com certificação SOC2/PCI-DSS.

## Estado da implementação (2026-11)
- Credenciais de agente aceitas no `/mcp`: chaves de API dinâmicas por workspace (`snt_...` armazenadas apenas como hash SHA-256 em `agent_api_keys`), chaves estáticas configuradas em `Sentinel:ApiKeys`, JWT (quando `Authentication:Mcp:Authority` está configurado; emissor, audiência, assinatura e expiração validados) e, apenas fora de Production com opt-in explícito `Sentinel:AllowMockTokens=true`, tokens mock. O servidor não emite tokens nem implementa o fluxo OAuth/PKCE para agentes; atua como *resource server* e publica `/.well-known/oauth-protected-resource` (RFC 9728) quando JWT está configurado.
- O papel vem do vínculo em `memberships`, não do token. Sem vínculo: `NOT_FOUND`; papel ou escopo insuficiente: `FORBIDDEN`.
- Revisão web: `GET /review` exige apenas sessão web (sem sessão, redireciona para `/auth/setup` quando não há workspaces/usuários locais ou para `/auth/login`); `GET /api/review/state` exige sessão e vínculo ativo com qualquer papel, e só lista workspaces em que a identidade tem vínculo. As ações `POST /api/review/approve|reject|restore` exigem sessão, cabeçalho `X-CSRF-Token` (antiforgery do ASP.NET Core) e papel Maintainer. Qualquer cabeçalho `Authorization` nas rotas de revisão resulta em 403; o caso de uso também recusa aprovação fora do canal web.
- Login web: contas locais PBKDF2-SHA256 (`local_users`, criadas via `/auth/setup` — que em `Production` exige `ADMIN_API_KEY` — e gerenciadas em `/api/admin/.../users`) ou OIDC (`code + PKCE`). `Sentinel:Web:EnableDevLogin=true` fica desabilitado por padrão e reservado apenas a suítes de teste fora de `Production`.
- RLS: a migração 003 aplica `FORCE ROW LEVEL SECURITY` e cria o papel `sentinel_app` sem BYPASSRLS; em Production a API recusa iniciar se o login do banco ignorar RLS.
- O princípio de quatro olhos não foi implementado (um Maintainer basta).
