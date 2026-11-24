# Arquitetura, Módulos e Funcionamento do Sentinel-MCP

Este documento descreve em detalhes **o que existe no sistema** e **como cada camada funciona** em conjunto para prover memória arquitetural governada para agentes de IA.

---

## 1. Visão Geral da Solução

O repositório está organizado em três projetos principais (.NET 9, compatível com .NET 10) + migrações SQL + suíte em três níveis de teste:

| Projeto / Pasta | Responsabilidade |
| --- | --- |
| `src/Sentinel.Core` | Domínio puro (sem dependência de rede ou banco): invariantes, casos de uso (`ServicoDeMemoria`), redutores determinísticos (`RedutorDeDecisao`, `RedutorDeIncidente`), contratos de repositório e exceções tipadas. |
| `src/Sentinel.Persistence` | Dois provedores que implementam exatamente os mesmos contratos de `Sentinel.Core`: `RepositorioSentinelEmMemoria` (desenvolvimento e demonstração) e `RepositorioSentinelPostgreSql` (produção com Npgsql + SQL puro + RLS + migrador com lock consultivo). |
| `src/Sentinel.Api` | Servidor HTTP ASP.NET Core e CLI `--stdio`: autenticação de agentes (`SecurityContext`), endpoints MCP (`POST /mcp`, `/mcp/sse`, `--stdio`), API de Revisão Humana (`/review` + `/api/review/*`), API Administrativa (`/api/admin/*`), diagnóstico OpenTelemetry e frontend estático com CSP estrita. |
| `db/migrations` | Scripts SQL numerados `001` a `004` (schema append-only, RLS + `FORCE RLS`, `tsvector`/GIN, integridade e permissões granulares para `sentinel_app`). |
| `db/fixtures` | `001_seed_demo.sql` com dois workspaces isolados (`11111111-...` e `22222222-...`), projetos e vínculos de demonstração. |
| `tests/` | `Sentinel.UnitTests` (invariantes, redutores, idempotência e autorização), `Sentinel.ContractTests` (protocolo MCP, schemas e segurança HTTP) e `Sentinel.IntegrationTests` (critérios AC-01 a AC-08 e API Admin executados tanto em Memória quanto contra PostgreSQL 16 via Testcontainers). |

---

## 2. Fluxo de Governança (Agente Propõe -> Humano Decide -> Memória Fica Auditável)

1. **Descoberta (`list_workspaces` / `sentinel://workspaces`)**:
   O agente conecta no MCP e descobre exatamente quais workspaces, papéis e projetos sua credencial pode acessar.
2. **Consulta com Comprovante (`query_context`)**:
   Antes de editar código ou tomar decisão, o agente consulta decisões vigentes e incidentes relevantes. Cada resposta gera um **`queryId`** e persiste um **comprovante SHA-256** (`context_receipts` + `context_receipt_items`) congelando `recordVersion` e `streamVersion` exatos lidos naquele instante.
3. **Proposta com Controle Otimista (`propose_decision`)**:
   Um agente com papel `Contributor` (ou `Maintainer`) envia uma proposta informando `expectedVersion` (`0` para tópico novo, ou a versão de fluxo atual para evoluir um tópico existente) e `idempotencyKey`.
   - O sistema **não altera** `decision_current` imediatamente: a proposta entra apenas na fila de revisão (`decision_pending_proposals`) mantendo a decisão vigente intocada até revisão humana.
   - Se já existir outra proposta pendente no mesmo tópico, a operação retorna `PROPOSAL_PENDING` (`409`), evitando sobrescrita silenciosa antes da revisão.
   - **Regra de canal**: tentativas de um agente aprovar, rejeitar ou restaurar diretamente pelo canal `Mcp` são bloqueadas (`CHANNEL_FORBIDDEN`) em `ServicoDeMemoria`.
4. **Registro Factual de Incidentes (`log_incident`)**:
   Falhas de produção e post-mortems podem ser registrados diretamente por contribuidores (`incident_current`) sem exigir aprovação prévia, preservando `occurredAt`, `technicalKeys` e evidências `https://`.
5. **Revisão Humana (`/review`)**:
   No console web autenticado (`OIDC` ou `DevLogin` fora de Production, protegido por cookie `HttpOnly` + `IAntiforgery` CSRF), o mantenedor:
   - Visualiza **comparação lado a lado** entre a decisão vigente e a proposta pendente (título, resumo, justificativa, chaves técnicas e links `https://`);
   - **Aprova** (promove para `decision_current`), **Rejeita** (registra motivo e mantém versão anterior vigente) ou **Restaura** qualquer versão histórica para reavaliação (`POST /api/review/restore`);
   - Inspeciona a **Linha do Tempo (`GET /api/review/timeline`)** e audita comprovantes no **Verificador SHA-256 (`GET /api/review/receipt`)**.

---

## 3. Catálogo de Interface MCP

### Ferramentas (`tools/list` / `tools/call`)
| Ferramenta | Escopo exigido | Papel mínimo | Efeito / Características |
| --- | --- | --- | --- |
| `list_workspaces` | `read:memory` | Qualquer vínculo ativo | Somente leitura (`readOnlyHint: true`). Lista workspaces, papel e projetos acessíveis à credencial. |
| `query_context` | `read:memory` | `Reader` | Busca lexical priorizando `technicalKeys` exatas + `tsvector` + fallback `ILIKE`; emite `queryId` e recibo SHA-256. |
| `get_timeline` | `read:memory` | `Reader` | Retorna eventos em ordem cronológica/paginada por cursor (`beforeVersion`) e o estado reconstituído (`atVersion`). |
| `get_context_receipt` | `read:memory` | Autor do recibo ou `Maintainer` | Valida e retorna o comprovante com recálculo de `digest` SHA-256 (`integrityVerified`). |
| `propose_decision` | `write:decision` | `Contributor` | Submete proposta com `expectedVersion`, `idempotencyKey`, evidências `https://` e bloqueio de proposta duplicada pendente. |
| `log_incident` | `write:incident` | `Contributor` | Grava incidente append-only em `incident_current` e no stream de eventos com `occurredAt`. |

Todas as 6 ferramentas publicam `inputSchema`, `outputSchema` e `annotations`, retornam `structuredContent` e incluem o JSON completo dentro de `content[0].text` para clientes que leem apenas conteúdo textual.

### Resources (`resources/list`, `resources/templates/list`, `resources/read`)
- `sentinel://workspaces` — Lista de workspaces e projetos da identidade autenticada.
- `sentinel://guide` — Guia operacional em Markdown para agentes MCP.
- `sentinel://workspaces/{workspaceId}/projects/{projectId}/decisions` — Decisões vigentes do projeto (template).
- `sentinel://workspaces/{workspaceId}/projects/{projectId}/incidents` — Incidentes do projeto (template).

### Prompts (`prompts/list`, `prompts/get`)
- `consult_context_before_change` — Roteiro guiado de leitura de contexto antes de alterar código.
- `propose_architecture_decision` — Fluxo guiado para propor decisão arquitetural com controle de versão.
- `report_incident_rca` — Fluxo guiado para registro estruturado de incidente e RCA.

---

## 4. Garantias de Engenharia, Segurança e Banco de Dados

### Camada Única de Autorização (`ServicoDeMemoria`)
- Validação de invariantes (`ValidadorDeInvariantes`), checagem de papel (`Reader` / `Contributor` / `Maintainer`), isolamento por canal (`Mcp` / `Web` / `AdminSistema`) e controle multi-tenant vivem em um único lugar: `src/Sentinel.Core/CasosDeUso/ServicoDeMemoria.cs`.
- Nem `POST /mcp`, nem `/mcp/sse`, nem `/api/review/*`, nem `/api/admin/*` duplicam regras de domínio.

### Event Sourcing e Reconstrução Determinística
- Os eventos são gravados apenas em `memory_events` (ligados a `memory_payloads` imutáveis indexados por hash SHA-256).
- `RedutorDeDecisao` e `RedutorDeIncidente` são as **únicas fontes** de projeção (`decision_current`, `decision_pending_proposals`, `incident_current`), permitindo `--rebuild-projections` completo ou por workspace sem perda de `recordVersion`, `streamVersion` ou `occurredAt`.
- No PostgreSQL, triggers (`prevent_mutation` / `trg_memory_events_append_only`) e ausência de privilégios `UPDATE`/`DELETE` para `sentinel_app` impedem mutação ou remoção retroativa dos logs.

### Multi-Tenancy e Row Level Security (RLS)
- Toda operação de negócio abre transação e executa `SELECT set_config('app.current_workspace_id', ..., true)` antes de consultar ou gravar.
- A API operacional conecta usando o papel **não-superusuário** `sentinel_app_login` (membro de `sentinel_app`), sujeito a `ENABLE` e `FORCE ROW LEVEL SECURITY` em todas as tabelas com escopo de workspace.
- Permissões granulares foram completadas na migração `004_admin_grants_and_rls.sql` para suportar criação administrativa de workspaces, projetos e vínculos sem elevar privilégios da aplicação.

### Segurança HTTP e Web
- **CSP estrita** (`default-src 'self'; script-src 'self'; ...`) e `X-Frame-Options: DENY` em `/` e `/review`, sem scripts inline nem dependências externas de CDN.
- Sanitização de evidências: somente URLs `https://` são aceitas na entrada (`ValidadorDeInvariantes`) e validadas novamente antes de virar `<a href>` no frontend.
- Rate limiting particionado por endpoint/token (`mcp`, `web`, `login`) e limites configuráveis.
- **Admin API (`/api/admin/*`)**: criação global de workspaces exige `X-Admin-Key` (comparação em tempo constante via `CryptographicOperations.FixedTimeEquals`), enquanto mantenedores web podem administrar projetos/vínculos **apenas do próprio workspace** mediante `X-CSRF-Token`.

---

## 5. Observabilidade, CLI e Operação

- **Telemetria OpenTelemetry**: Tracing (`Sentinel-MCP`), métricas ASP.NET Core, HttpClient e Runtime, mais contadores dedicados `sentinel_mcp_tool_calls_total` e `sentinel_mcp_tool_errors_total`, exportáveis via OTLP (`OTEL_EXPORTER_OTLP_ENDPOINT`).
- **Health Checks**:
  - `GET /healthz`: liveness básico da API.
  - `GET /readyz`: readiness real (no PostgreSQL executa `SELECT 1` e verifica se há migrações pendentes).
- **Retenção Programada**: `ServicoRetencaoBanco` remove apenas dados operacionais expirados (`command_receipts`, `context_receipts`) preservando integralmente o log histórico append-only.
- **Comandos CLI da API**:
  - `--migrate` — Aplica migrações SQL pendentes com `pg_advisory_lock` e sai.
  - `--seed-demo` — Aplica dados de demonstração (`InMemory`: mantém rodando; `PostgreSQL`: semeia, reconstrói projeções e sai com código `0`).
  - `--rebuild-projections [--workspace-id <uuid>]` — Reconstitui projeções a partir dos eventos e sai.
  - `--stdio [--token <valor>]` — Sobe apenas o servidor MCP sobre stdin/stdout (logs direcionados para `stderr`).
