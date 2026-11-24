# Arquitetura, Módulos e Funcionamento do Sentinel-MCP

Este documento descreve em detalhes **o que existe no sistema** e **como cada camada funciona** para entregar memória arquitetural governada, auditável e livre de dados mock para múltiplos agentes de IA.

---

## 1. Visão Geral da Solução

| Projeto / Pasta | Responsabilidade |
| --- | --- |
| `src/Sentinel.Core` | Domínio puro (sem dependência de banco ou HTTP): invariantes (`ValidadorDeInvariantes`), casos de uso (`ServicoDeMemoria`), criptografia local (`SegurancaLocal`: PBKDF2-SHA256 e tokens `snt_` SHA-256), redutores (`RedutorDeDecisao`, `RedutorDeIncidente`), contratos (`IRepositorios`) e exceções tipadas. |
| `src/Sentinel.Persistence` | Persistência simétrica sob os mesmos contratos: `RepositorioSentinelEmMemoria` (modo local sem dependências externas, com persistência atômica opcional em `Sentinel:LocalDataFile`) e `RepositorioSentinelPostgreSql` (Npgsql, SQL parametrizado, RLS por transação e migrador com `pg_advisory_lock`). |
| `src/Sentinel.Api` | Servidor ASP.NET Core (.NET 9) e modo CLI `--stdio`: setup inicial `/auth/setup`, login local PBKDF2 / OIDC PKCE (`/auth/login`), autenticação de agentes (`SecurityContext`), endpoints MCP (`POST /mcp`, `/mcp/sse`, `--stdio`), revisão (`/api/review/*`), administração (`/api/admin/*`), OpenTelemetry e console web com CSP estrita. |
| `db/migrations` | Migrações `001` a `005`: schema append-only, RLS + `FORCE ROW LEVEL SECURITY`, colunas geradas `tsvector`/GIN, integridade, grants mínimos para `sentinel_app`, `local_users` e `agent_api_keys`. |
| `tests/` | `Sentinel.UnitTests` (invariantes, redutores, idempotência, autorização, PBKDF2 e API keys), `Sentinel.ContractTests` (protocolo MCP, schemas, resources, prompts e CSP) e `Sentinel.IntegrationTests` (AC-01 a AC-08, `AdminApiTests` e `FluxoSemMocksTests`). |

---

## 2. Primeiro Uso e Autenticação Real (Zero Mocks)

1. **Setup Inicial (`/auth/setup`)**:
   Quando o banco/arquivo local ainda não possui workspaces nem usuários e OIDC não está configurado, `/review` e `/auth/login` redirecionam automaticamente para `/auth/setup`. Ali o administrador cria o primeiro **Workspace**, o primeiro **Projeto** e sua **conta Maintainer** (senha armazenada como `pbkdf2-sha256$210000$<salt>$<hash>`).
2. **Login Web (`/auth/login`)**:
   Autentica com usuário + senha verificados em tempo constante (`CryptographicOperations.FixedTimeEquals`) — ou delega para **OIDC + PKCE** quando `Authentication:Web:Authority` está configurado — emitindo cookie `HttpOnly` + proteção `IAntiforgery` (`X-CSRF-Token`) em toda mutação.
3. **Chaves de API para Agentes (`agent_api_keys`)**:
   Em **Conectar IAs (MCP)** ou **Administração**, o mantenedor cria chaves `snt_<256-bit>` associadas a um `subject`, `clientId`, papel e escopos (`read:memory`, `write:decision`, `write:incident`). O servidor persiste apenas o **hash SHA-256** + prefixo visível (`key_prefix`) e registra `last_used_at` no uso.
4. **Persistência Local Automática vs PostgreSQL**:
   - **Local (`ASPNETCORE_ENVIRONMENT=Development`)**: persiste tudo em `Sentinel:LocalDataFile` (`data/sentinel-local.json`) reconstruindo projeções a partir dos eventos ao reiniciar.
   - **Produção (`PostgreSQL`)**: executa sobre `sentinel_app_login` (sem privilégio de superusuário ou `BYPASSRLS`) com `ENABLE` e `FORCE ROW LEVEL SECURITY` e triggers bloqueando `UPDATE`/`DELETE`/`TRUNCATE` em `memory_events` e `memory_payloads`.

---

## 3. Ciclo de Governança (Agente Propõe -> Humano Decide -> Auditoria SHA-256)

1. **Descoberta (`list_workspaces` / `sentinel://workspaces`)**: retorna apenas workspaces e projetos vinculados à credencial do agente.
2. **Consulta (`query_context`)**: busca decisões `active` e incidentes `reported`, prioriza correspondência exata de `technicalKeys` sobre `tsvector` + fallback `ILIKE`, cria `queryId` e persiste o comprovante SHA-256 em `context_receipts`.
3. **Proposta (`propose_decision`)**: exige `expectedVersion` e `idempotencyKey`, links `https://` válidos e nunca altera a decisão vigente antes da revisão humana. Se já existir proposta pendente no tópico, retorna `409 PROPOSAL_PENDING`.
4. **Incidentes (`log_incident`)**: registra relatos técnicos e RCAs com `occurredAt` no histórico imutável.
5. **Console Web (`/review`)**:
   - **Visão Geral**: explica o fluxo em 4 etapas, acompanha checklist real e oferece UUIDs copiáveis.
   - **Revisões Pendentes**: compara lado a lado a decisão vigente e a nova proposta; aprova (`POST /api/review/approve`) ou rejeita com justificativa obrigatória (`POST /api/review/reject`).
   - **Decisões Vigentes**: exibe histórico por versão (`GET /api/review/timeline`) e permite restaurar versões anteriores (`POST /api/review/restore`, criando nova proposta sujeita à aprovação).
   - **Administração**: cria/exclui projetos vazios (projetos com eventos são bloqueados para proteger a trilha auditável), gera/revoga chaves `snt_...`, gerencia contas locais e vínculos — **toda exclusão/revogação exige confirmação digitada**.

---

## 4. Catálogo MCP (Tools, Resources e Prompts)

| Tipo | Identificador | Escopo / Função |
| --- | --- | --- |
| Tool | `list_workspaces` | `read:memory` — Lista workspaces, papel e projetos acessíveis à credencial. |
| Tool | `query_context` | `read:memory` — Busca lexical determinística e gera comprovante `queryId` SHA-256. |
| Tool | `get_timeline` | `read:memory` — Lista eventos paginados (`beforeVersion`) e reconstitui estado (`atVersion`). |
| Tool | `get_context_receipt` | `read:memory` — Verifica integridade SHA-256 do comprovante (autor ou `Maintainer`). |
| Tool | `propose_decision` | `write:decision` — Submete proposta com `expectedVersion` e `idempotencyKey`. |
| Tool | `log_incident` | `write:incident` — Registra incidente append-only com `occurredAt` e evidências HTTPS. |
| Resource | `sentinel://workspaces` | Catálogo JSON de workspaces/projetos da identidade autenticada. |
| Resource | `sentinel://guide` | Guia operacional Markdown para agentes MCP. |
| Template | `sentinel://workspaces/{workspaceId}/projects/{projectId}/decisions` | Decisões vigentes ativas de um projeto. |
| Template | `sentinel://workspaces/{workspaceId}/projects/{projectId}/incidents` | Incidentes reportados de um projeto. |
| Prompt | `consult_context_before_change` | Roteiro de descoberta e consulta antes de modificar código. |
| Prompt | `propose_architecture_decision` | Roteiro de proposta arquitetural versionada. |
| Prompt | `report_incident_rca` | Roteiro de registro de incidente e RCA. |
