# Arquitetura, Módulos e Fluxogramas — Sentinel-MCP

Este documento apresenta a arquitetura modular e os principais fluxos operacionais do **Sentinel-MCP**, válidos tanto no modo **local individual** (`Development` com `data/sentinel-local.json`) quanto em **produção organizacional** (`PostgreSQL` + `FORCE ROW LEVEL SECURITY`).

---

## 1. Visão em Camadas da Solução

```mermaid
flowchart TB
    subgraph Entrada["Canais de Entrada"]
        McpClients["Clientes MCP (Codex, Claude, Cursor, Gemini, VS Code)"]
        Browser["Revisor Humano (Console /review + /auth/*)"]
        AdminCli["Automacao / CI (X-Admin-Key -> /api/admin/*)"]
    end

    subgraph Api["Sentinel.Api (.NET 9 / ASP.NET Core)"]
        Middleware["ForwardedHeaders + CSP Estrita + Rate Limiter + OTel"]
        McpLayer["McpEndpointHandler + McpSdkAdapter + McpDispatcher"]
        WebLayer["WebReviewHandler + AdminEndpointHandler"]
        AuthAgent["SecurityContext (snt_ SHA-256, ApiKeys estaticas, JWT Bearer)"]
    end

    subgraph Core["Sentinel.Core (Dominio Puro)"]
        UseCases["ServicoDeMemoria (Unica camada de casos de uso e autorizacao)"]
        Invariants["ValidadorDeInvariantes + SegurancaLocal (PBKDF2 / SHA-256)"]
        Reducers["RedutorDeDecisao + RedutorDeIncidente"]
        Contracts["IRepositorios (Contratos simetricos)"]
    end

    subgraph Persistence["Sentinel.Persistence"]
        LocalJson["RepositorioSentinelEmMemoria (Snapshot JSON atomico opcional)"]
        Postgres["RepositorioSentinelPostgreSql (Npgsql, RLS por transacao, Triggers)"]
    end

    McpClients --> Middleware --> McpLayer --> AuthAgent --> UseCases
    Browser --> Middleware --> WebLayer --> UseCases
    AdminCli --> Middleware --> WebLayer
    UseCases --> Invariants
    UseCases --> Reducers
    UseCases --> Contracts
    Contracts --> LocalJson
    Contracts --> Postgres
```

| Projeto / Pasta | Responsabilidade |
| --- | --- |
| `src/Sentinel.Core` | Domínio puro sem dependência de banco ou HTTP: `ServicoDeMemoria`, `ValidadorDeInvariantes`, `SegurancaLocal` (PBKDF2-SHA256 210 mil iterações e geração de chaves `snt_`), `RedutorDeDecisao`, `RedutorDeIncidente`, `IRepositorios` e exceções tipadas. |
| `src/Sentinel.Persistence` | Persistência simétrica sob os mesmos contratos: `RepositorioSentinelEmMemoria` (dev/local com gravação atômica em `Sentinel:LocalDataFile`) e `RepositorioSentinelPostgreSql` (Npgsql, SQL parametrizado, RLS por transação e migrador com `pg_advisory_lock`). |
| `src/Sentinel.Api` | Servidor HTTP + modo `--stdio`: `Program.cs` (pipeline, DataProtection, ForwardedHeaders, RateLimiter, OTel), `/auth/setup`, `/auth/login`, `/review`, `/mcp`, `/mcp/sse`, `/api/admin/*` e console web com [Design System](../design/DESIGN_SYSTEM.md). |
| `db/migrations` | Migrações `001` a `006`: schema append-only, RLS + `FORCE ROW LEVEL SECURITY`, `tsvector`/GIN, integridade SHA-256, `local_users`, `agent_api_keys` e grants mínimos para `sentinel_app`. |
| `tests/` | `Sentinel.UnitTests` (44 testes), `Sentinel.ContractTests` (27 testes) e `Sentinel.IntegrationTests` (AC-01 a AC-08, `AdminApiTests` e `FluxoSemMocksTests` em InMemory e PostgreSQL Testcontainers). |

---

## 2. Fluxo de Autenticação, Onboarding e Separação de Canais

Nenhuma credencial de demonstração vem ativada por padrão (`AllowMockTokens=false`, `EnableDevLogin=false`, `SeedDemoData=false`).

```mermaid
flowchart TD
    Visit["Usuario acessa /review"] --> HasCookie{"Cookie sentinel_session valido?"}
    HasCookie -->|Nao| IsEmpty{"Sistema sem workspaces/usuarios?"}
    HasCookie -->|Sim| HasBind{"Tem vinculo ativo neste ambiente?"}

    HasBind -->|Nao| SignOut["Descarta sessao obsoleta (SignOut)"] --> IsEmpty
    HasBind -->|Sim| Console["Carrega Console /review (/api/review/state + CSRF)"]

    IsEmpty -->|Sim e sem OIDC| Setup["Redireciona para /auth/setup"]
    Setup --> CheckEnv{"Ambiente eh Production?"}
    CheckEnv -->|Sim| VerifyKey["Exige ADMIN_API_KEY valida (503 se ausente, 403 se invalida)"]
    CheckEnv -->|Nao| CreateAdmin["Cria Workspace + Projeto + Maintainer PBKDF2"]
    VerifyKey --> CreateAdmin --> Console

    IsEmpty -->|Nao| Login["Redireciona para /auth/login"]
    Login --> AuthMode{"OIDC configurado?"}
    AuthMode -->|Sim| Oidc["Challenge OIDC + PKCE (/auth/callback)"] --> Console
    AuthMode -->|Nao| LocalPwd["Form Usuario + Senha PBKDF2-SHA256"] --> Console
```

---

## 3. Ciclo de Vida de Estados de uma Decisão

Um tópico de decisão pode manter **uma decisão vigente (`active`)** e **no máximo uma proposta pendente (`proposed`)** simultaneamente sem perder o histórico de versões.

```mermaid
stateDiagram-v2
    [*] --> Proposed: "propose_decision (expectedVersion = 0)"
    Proposed --> Active: "POST /api/review/approve (Maintainer + Web + CSRF)"
    Proposed --> Rejected: "POST /api/review/reject (Motivo obrigatorio)"

    Active --> PendingUpdate: "propose_decision (expectedVersion = atual)"
    PendingUpdate --> SupersededAndActive: "POST /api/review/approve (Substitui vigente atomicamente)"
    PendingUpdate --> Active: "POST /api/review/reject (Preserva vigente anterior)"

    Active --> PendingRestore: "POST /api/review/restore (Cria proposta a partir de vN)"
    PendingRestore --> SupersededAndActive: "POST /api/review/approve"
```

---

## 4. Concorrência Otimista, Idempotência e Comprovante SHA-256

```mermaid
sequenceDiagram
    autonumber
    actor Agente as Agente MCP
    participant Api as McpDispatcher
    participant Svc as ServicoDeMemoria
    participant Pg as Repositorio (Transacao ACID)

    Agente->>Api: propose_decision (expectedVersion, idempotencyKey, payload)
    Api->>Svc: ProporDecisaoAsync(ator, entrada)
    Svc->>Svc: Valida invariantes + HTTPS + papel Contributor/Maintainer
    Svc->>Pg: BEGIN TRANSACTION + set_config(app.current_workspace_id)
    Pg->>Pg: pg_advisory_xact_lock(idempotencyKey)
    alt Recibo ja existe em command_receipts
        Pg-->>Svc: Mesmo hash de argumentos -> devolve resultado original (ou 409 IDEMPOTENCY_KEY_REUSED)
    else Comando novo
        Pg->>Pg: SELECT ... FOR UPDATE em decision_topics
        alt Versao diverge ou ja existe proposta pendente
            Pg-->>Agente: 409 VERSION_CONFLICT ou 409 PROPOSAL_PENDING
        else Versao compativel
            Pg->>Pg: INSERT memory_payloads + memory_events (append-only)
            Pg->>Pg: Atualiza decision_current e decision_pending_proposals via RedutorDeDecisao
            Pg->>Pg: INSERT command_receipts + COMMIT
            Pg-->>Agente: topicId, proposalId, streamVersion, status="proposed"
        end
    end
```

---

## 5. Isolamento Multi-Tenant com PostgreSQL `FORCE ROW LEVEL SECURITY`

Em produção, a API nunca conecta como superusuário: `Program.cs` recusa iniciar se o login em `ConnectionStrings:Database` possuir `rolsuper` ou `rolbypassrls`.

```mermaid
flowchart LR
    Req["Requisicao MCP ou Web"] --> App["Sentinel.Api (Conecta como sentinel_app_login)"]
    App --> Tx["BEGIN Transacao -> SELECT set_config('app.current_workspace_id', wsId, true)"]
    Tx --> Rls["PostgreSQL FORCE ROW LEVEL SECURITY"]
    Rls --> Match{"workspace_id == current_setting('app.current_workspace_id')?"}
    Match -->|Sim| Data["Acessa projects, memberships, events, current e api_keys do tenant"]
    Match -->|Nao| Empty["0 linhas visiveis -> API retorna NOT_FOUND uniforme"]
```

---

## 6. Catálogo Completo MCP & Administração

### Ferramentas, Resources e Prompts MCP

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

### Rotas Administrativas (`/api/admin/*`) e Revisão (`/api/review/*`)

- **Workspaces & Projetos**: `GET/POST /api/admin/workspaces`, `GET/PUT /api/admin/workspaces/{workspaceId}`, `GET/POST /api/admin/workspaces/{workspaceId}/projects`, `DELETE .../projects/{projectId}` (`409` se houver histórico).
- **Credenciais & Vínculos**: `GET/POST/DELETE .../api-keys`, `GET/POST .../users`, `GET/POST/DELETE .../memberships`.
- **Governança Humana**: `GET /api/review/state`, `GET /api/review/timeline`, `GET /api/review/receipt`, `POST /api/review/approve`, `/reject`, `/restore` (exclusivos de sessão web `Maintainer` com `X-CSRF-Token`).
