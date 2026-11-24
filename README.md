<p align="center">
  <img src="src/Sentinel.Api/wwwroot/sentinel-verde.svg" width="104" height="104" alt="Sentinel Logo" />
</p>

<h1 align="center">Sentinel-MCP</h1>

<p align="center">
  <strong>A Memória da sua Engenharia</strong><br />
  Decisões técnicas e incidentes versionados como eventos imutáveis, consultados por agentes de IA via MCP e aprovados exclusivamente por pessoas.
</p>

<p align="center">
  <img src="https://img.shields.io/badge/.NET-9.0-512BD4?style=flat-square&logo=dotnet&logoColor=white" alt=".NET 9" />
  <img src="https://img.shields.io/badge/PostgreSQL-16%2B-336791?style=flat-square&logo=postgresql&logoColor=white" alt="PostgreSQL 16+" />
  <img src="https://img.shields.io/badge/License-MIT-yellow.svg?style=flat-square" alt="Licença MIT" />
</p>

---

## Sumário

- [Visão geral](#visão-geral)
- [Arquitetura](#arquitetura)
- [Tools, Resources e Prompts MCP](#tools-resources-e-prompts-mcp)
- [Conectando clientes MCP](#conectando-clientes-mcp)
- [Execução local](#execução-local)
- [Configuração e segredos](#configuração-e-segredos)
- [Segurança](#segurança)
- [Operação](#operação)
- [Testes](#testes)
- [Critérios de aceite (AC-01 a AC-08)](#critérios-de-aceite-ac-01-a-ac-08)
- [Estrutura da solução](#estrutura-da-solução)
- [Limitações conhecidas e pendências](#limitações-conhecidas-e-pendências)
- [Documentação complementar](#documentação-complementar)
- [Licença](#licença)

---

## Visão geral

O **Sentinel-MCP** é um servidor MCP (*Model Context Protocol*) em C#/.NET 9 que dá a agentes de desenvolvimento (**OpenAI Codex**, **Claude Code**, **Cursor**, **Gemini CLI**, **VS Code Copilot** e outros clientes MCP) acesso às decisões arquiteturais vigentes e aos incidentes registrados de uma organização.

Problemas que ele ataca:

1. **Propostas recicladas**: um agente sugere uma abordagem que a equipe já avaliou e descartou.
2. **Incidentes sem histórico**: um agente analisa uma falha sem ver o RCA e a mitigação de um caso anterior.
3. **Conhecimento fragmentado**: decisões ficam espalhadas em PRs, chats e ADRs sem indicação de vigência.

Ciclo de governança:

```
┌─────────────────┐       propose_decision       ┌─────────────────────┐
│  Agente de IA   │ ───────────────────────────> │  Sentinel (MCP)     │
│  (cliente MCP)  │                              │  proposta pendente  │
└─────────────────┘                              └──────────┬──────────┘
                                                            │
                                                            ▼
┌─────────────────┐   aprovação / rejeição       ┌─────────────────────┐
│   Mantenedor    │ ───────────────────────────> │  /review            │
│   (pessoa)      │   sessão web + CSRF          │  (papel Maintainer) │
└─────────────────┘                              └──────────┬──────────┘
                                                            │
                                                            ▼
┌─────────────────┐        query_context         ┌─────────────────────┐
│  Outro agente   │ <─────────────────────────── │ Decisão vigente     │
│                 │  + comprovante (digest)      │ + incidentes        │
└─────────────────┘                              └─────────────────────┘
```

Garantias implementadas:

| Garantia | Como é implementada |
| --- | --- |
| **Histórico imutável** | Cada proposta, aprovação, rejeição, substituição e incidente é um evento em `memory_events` com payload em `memory_payloads`. No PostgreSQL, triggers impedem `UPDATE`, `DELETE` e `TRUNCATE` nessas tabelas. |
| **Projeções derivadas** | `decision_current`, `incident_current` e `decision_pending_proposals` são sempre calculadas pelo redutor (`RedutorDeDecisao` / `RedutorDeIncidente`) na mesma transação do evento. A reconstrução usa o mesmo redutor. |
| **Concorrência por tópico** | `expectedVersion` obrigatório; o tópico é travado (`FOR UPDATE`) e divergências retornam `VERSION_CONFLICT`. A primeira proposta usa `INSERT ... ON CONFLICT`. Só existe **uma proposta pendente por tópico**: nova proposta enquanto há pendente retorna `PROPOSAL_PENDING`. |
| **Idempotência** | Toda escrita exige `idempotencyKey`. Retentativas com os mesmos argumentos devolvem o resultado original; argumentos diferentes retornam `IDEMPOTENCY_KEY_REUSED`. No PostgreSQL, um lock consultivo por chave serializa retentativas concorrentes. |
| **Isolamento por workspace** | Vínculo ativo (`memberships`) verificado em cada caso de uso; sem vínculo a resposta é `NOT_FOUND`. No PostgreSQL, RLS com `FORCE ROW LEVEL SECURITY` e `app.current_workspace_id` definido por transação. |
| **Aprovação somente humana** | Nenhuma tool MCP aprova, rejeita ou restaura. Essas ações exigem sessão web, token CSRF e papel Maintainer. |
| **Comprovante de consulta** | Cada `query_context` grava os itens entregues (IDs e versões, em ordem) com um digest SHA-256 canônico. É um registro de integridade verificável pelo servidor, **não uma assinatura digital**. |

---

## Arquitetura

```mermaid
flowchart TB
    subgraph Clientes
        A1["Clientes MCP (Claude Code, Cursor...)"]
        H1["Mantenedor (navegador)"]
    end

    subgraph Api["Sentinel.Api (ASP.NET Core / .NET 9)"]
        McpEp["POST /mcp (JSON-RPC, sem sessão)"]
        WebRev["/review + /api/review/* (cookie + CSRF)"]
        Auth["Credenciais de agente: JWT, chave de API ou mock (dev)"]
    end

    subgraph Core["Sentinel.Core"]
        Servico["CasosDeUso/ServicoDeMemoria"]
        Valid["Validacao/ValidadorDeInvariantes"]
        Red["Redutores (decisão e incidente)"]
        Contr["Contratos (interfaces de repositório)"]
    end

    subgraph Pers["Sentinel.Persistence"]
        Mem["EmMemoria (desenvolvimento/testes)"]
        Pg["PostgreSql (Npgsql, SQL puro, RLS)"]
    end

    A1 -->|Authorization: Bearer| McpEp
    H1 -->|OIDC ou login de desenvolvimento| WebRev
    McpEp --> Auth --> Servico
    WebRev --> Servico
    Servico --> Valid
    Servico --> Red
    Servico --> Contr
    Contr --> Mem
    Contr --> Pg
```

- **Uma única camada de casos de uso** (`src/Sentinel.Core/CasosDeUso/ServicoDeMemoria.cs`): validação de entrada (via `ValidadorDeInvariantes`), vínculo e papel no workspace e a regra de que aprovação/rejeição/restauração só aceitam o canal web. O endpoint MCP e a página de revisão apenas autenticam e traduzem.
- **Contratos de repositório** em `src/Sentinel.Core/Contratos`, com duas implementações: em memória e PostgreSQL.
- **Armazenamento**: `Sentinel:StorageProvider` = `InMemory` (padrão; não persiste, proibido em Production) ou `PostgreSQL`.

---

## Tools, Resources e Prompts MCP

### Ferramentas (`tools/list` / `tools/call`)

| Tool | Descrição | Exige |
| --- | --- | --- |
| `list_workspaces` | Descobre os workspaces, papel do agente e projetos acessíveis à credencial autenticada (`readOnlyHint: true`). | escopo `read:memory` + vínculo ativo |
| `propose_decision` | Registra uma proposta num tópico (`topicKey`), com justificativa e evidências HTTPS. Fica pendente até aprovação humana. | escopo `write:decision` + papel Contributor ou Maintainer |
| `log_incident` | Registra um incidente (`reported`) com chaves técnicas e evidências. | escopo `write:incident` + papel Contributor ou Maintainer |
| `query_context` | Busca decisões `active` e/ou incidentes `reported` e grava um comprovante. `kind`: `decision` (padrão), `incident` ou `both`; `limit` de 1 a 20 (padrão 10). | escopo `read:memory` + papel Reader ou superior |
| `get_timeline` | Reconstitui o estado de um fluxo numa versão (`atVersion`, padrão: atual) e lista eventos em ordem decrescente; `limit` de 1 a 50 (padrão 20); use `nextBeforeVersion` como `beforeVersion` para a próxima página. O estado reconstituído independe da página. | escopo `read:memory` + papel Reader ou superior |
| `get_context_receipt` | Devolve o comprovante de uma consulta: itens ordenados com versões, `digest`, `digestAlgorithm: "sha256"` e `integrityVerified` (o servidor recalcula o digest). | escopo `read:memory`; visível ao autor da consulta e a Maintainers do workspace (demais recebem `NOT_FOUND`) |

Todas as 6 ferramentas publicam `inputSchema`, `outputSchema` e `annotations`, retornam `structuredContent` e incluem o JSON completo dentro de `content[0].text` para clientes que consomem apenas texto.

### Resources & Prompts
- **Resources (`resources/list`, `resources/read`)**: `sentinel://workspaces`, `sentinel://guide`, além dos templates `sentinel://workspaces/{workspaceId}/projects/{projectId}/decisions` e `sentinel://workspaces/{workspaceId}/projects/{projectId}/incidents`.
- **Prompts (`prompts/list`, `prompts/get`)**: `consult_context_before_change`, `propose_architecture_decision` e `report_incident_rca`.

Erros de tool voltam com `isError: true` e `structuredContent.code` igual a um destes códigos: `VALIDATION_ERROR`, `NOT_FOUND`, `VERSION_CONFLICT`, `PROPOSAL_PENDING`, `IDEMPOTENCY_KEY_REUSED`, `FORBIDDEN`, `UNAUTHORIZED`, `TEMPORARILY_UNAVAILABLE`, `INTERNAL_ERROR`. Em `INTERNAL_ERROR` a mensagem é genérica e traz apenas um ID de correlação; o detalhe fica no log do servidor.

### Busca (`query_context`)

- Candidatos: `search_vector @@ websearch_to_tsquery('portuguese' | 'simple', query)`, chave técnica normalizada igual a uma das palavras da consulta ou à consulta inteira, ou `ILIKE` em título/resumo (com escape de `%` e `_`).
- Escopo: somente o workspace e o projeto informados; decisões com estado `active` e incidentes `reported`.
- Ordem determinística: correspondência exata de chave técnica, `ts_rank_cd`, data (desc.) e ID.
- `search_vector` e `technical_keys_norm` são colunas geradas com índice GIN (migração 003).

### Exemplo: `propose_decision`

IDs do conjunto de demonstração (`--seed-demo` / InMemory em Development): workspace `11111111-1111-1111-1111-111111111111`, projeto `aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa`.

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "tools/call",
  "params": {
    "name": "propose_decision",
    "arguments": {
      "workspaceId": "11111111-1111-1111-1111-111111111111",
      "projectId": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
      "topicKey": "pix-idempotency",
      "expectedVersion": 0,
      "title": "Chave de idempotência obrigatória na cobrança Pix",
      "summary": "Toda criação de cobrança Pix deve enviar X-Idempotency-Key com retenção mínima de 24 horas.",
      "rationale": "Evita cobranças duplicadas em retentativas incertas do gateway.",
      "technicalKeys": ["pix", "idempotency", "billing"],
      "evidence": [
        { "type": "adr", "label": "ADR de idempotência de pagamentos", "url": "https://example.com/adr/003-payment-idempotency" }
      ],
      "idempotencyKey": "prop-pix-idem-001"
    }
  }
}
```

`expectedVersion` é `0` para um tópico novo; para um tópico existente, informe a versão atual do fluxo (obtida, por exemplo, com `get_timeline`).

### Exemplo: `query_context`

```json
{
  "jsonrpc": "2.0",
  "id": 2,
  "method": "tools/call",
  "params": {
    "name": "query_context",
    "arguments": {
      "workspaceId": "11111111-1111-1111-1111-111111111111",
      "projectId": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
      "query": "idempotency",
      "kind": "both",
      "limit": 5
    }
  }
}
```

A resposta traz `queryId`, `matchStatus` (`found` ou `none`), `searchMode` (`textual`) e `items` com IDs, versões, estado, resumo e evidências. O `queryId` pode ser usado depois em `get_context_receipt`. O comprovante não guarda o texto da consulta.

---

## Conectando clientes MCP

O Sentinel-MCP oferece três modos de transporte compatíveis com qualquer cliente MCP (`2025-11-25`, `2025-06-18`, `2025-03-26`, `2024-11-05`):
- **`POST /mcp`** — Streamable HTTP stateless JSON-RPC (recomendado).
- **`/mcp/sse`** — Endpoint HTTP stateful via SDK oficial `ModelContextProtocol.AspNetCore`.
- **`--stdio`** — Transporte local via CLI (`dotnet run --no-build --project src/Sentinel.Api -- --stdio`).

**Toda requisição HTTP precisa de `Authorization: Bearer <token>`**. Após criar seu workspace em `/auth/setup`, acesse `/review` → **Conectar IAs (MCP)** para emitir uma **chave de API real por workspace** (`snt_...`, armazenada em hash SHA-256) com os escopos mínimos desejados (`read:memory`, `write:decision`, `write:incident`). Veja o guia passo a passo com screenshots/blocos prontos em [docs/guides/conectando-agentes-mcp.md](docs/guides/conectando-agentes-mcp.md).

### OpenAI Codex (`codex-cli` e Extensão VS Code)

Via CLI com HTTP (usando a chave `snt_...` emitida no console):
```powershell
$env:SENTINEL_MCP_TOKEN = "snt_sua_chave_real_aqui"
codex mcp add sentinel --url http://localhost:5000/mcp --bearer-token-env-var SENTINEL_MCP_TOKEN
```

No Windows/VS Code (ou quando a extensão não herda variáveis do terminal), declare o header explícito em `~/.codex/config.toml` (`%USERPROFILE%\.codex\config.toml`):
```toml
[mcp_servers.sentinel]
url = "http://localhost:5000/mcp"

[mcp_servers.sentinel.http_headers]
Authorization = "Bearer snt_sua_chave_real_aqui"
```

Ou via **STDIO** no `~/.codex/config.toml` (sem depender de porta HTTP — lembre-se de informar `--token` com sua chave `snt_...` ou configurar `SENTINEL_MCP_TOKEN`):
```toml
[mcp_servers.sentinel]
command = "dotnet"
args = ["run", "--no-build", "--no-launch-profile", "--project", "./src/Sentinel.Api", "--", "--stdio", "--token", "snt_sua_chave_real_aqui"]

[mcp_servers.sentinel.env]
ASPNETCORE_ENVIRONMENT = "Development"
```

### Claude Code

```bash
claude mcp add --transport http sentinel http://localhost:5000/mcp --header "Authorization: Bearer snt_sua_chave_real_aqui"
```

### Cursor

Em `.cursor/mcp.json`:

```json
{
  "mcpServers": {
    "sentinel": {
      "url": "http://localhost:5000/mcp",
      "headers": {
        "Authorization": "Bearer snt_sua_chave_real_aqui"
      }
    }
  }
}
```

### Gemini CLI e VS Code Copilot

- **Gemini CLI** (`~/.gemini/settings.json`): configure `httpUrl: "http://localhost:5000/mcp"` e `headers.Authorization` com `Bearer snt_sua_chave_real_aqui`.
- **VS Code** (`.vscode/mcp.json`): configure `type: "http"`, `url: "http://localhost:5000/mcp"` e `headers.Authorization` usando `input` seguro para a chave `snt_...`.

Exemplos completos e diagnóstico em [docs/guides/conectando-agentes-mcp.md](docs/guides/conectando-agentes-mcp.md).

Não versione arquivos com tokens reais. O servidor não implementa fluxo OAuth próprio: quando JWT está configurado, o cliente obtém o token no provedor de identidade e o envia no cabeçalho.

---

## Execução local

Pré-requisitos: .NET 9 SDK (ou .NET 10 com roll-forward); Docker (para o compose e para os testes de PostgreSQL).

> **Ambiente:** em uso direto via `dotnet run` sem perfil de launch (`--no-launch-profile`) ou binário publicado, o ambiente padrão do ASP.NET Core é **Production**, que exige PostgreSQL (`ConnectionStrings:Database`). No repositório há `src/Sentinel.Api/Properties/launchSettings.json` definindo `ASPNETCORE_ENVIRONMENT=Development` e `http://localhost:5000`, mas exportar a variável explicitamente garante previsibilidade em qualquer shell.

### Opção 1: Modo Local Persistido (Development)

`appsettings.Development.json` habilita armazenamento local persistido em `data/sentinel-local.json` (`Sentinel:LocalDataFile`), **sem dados mock** (`SeedDemoData: false`), **sem tokens mock** (`AllowMockTokens: false`) e **sem login de demonstração** (`EnableDevLogin: false`). Seus workspaces, projetos, contas locais, chaves de API e eventos permanecem salvos entre reinícios.

```bash
# bash
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Sentinel.Api
```

```powershell
# PowerShell
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet run --project src/Sentinel.Api
```

A API escuta em `http://localhost:5000`:

- **Landing**: `http://localhost:5000/`
- **Configuração inicial (`/auth/setup`)**: na primeira visita a `http://localhost:5000/review`, como não há workspaces cadastrados, você cria o primeiro administrador local (senha com hash PBKDF2-SHA256), o primeiro workspace e o primeiro projeto reais.
- **Chave de API real (`snt_...`)**: após entrar em `/review`, abra **Conectar IAs (MCP)** na barra lateral para gerar sua chave de agente por workspace e copiar o trecho pronto para Codex, Claude Code, Cursor, Gemini CLI ou VS Code.

Smoke test (após gerar sua chave `snt_...`):

```bash
curl http://localhost:5000/healthz
curl -X POST http://localhost:5000/mcp \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer snt_sua_chave_real_aqui" \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"smoke","version":"1.0"}}}'
```

### Opção 2: Docker Compose com PostgreSQL

```bash
cp .env.example .env          # defina POSTGRES_PASSWORD, SENTINEL_APP_DB_PASSWORD e ADMIN_API_KEY
docker compose up -d --build
curl http://localhost:5000/readyz
```

Serviços:

| Serviço | Função |
| --- | --- |
| `postgres` | PostgreSQL. Na **primeira** inicialização do volume, `db/docker/01-create-app-role.sh` cria o papel `sentinel_app` (NOLOGIN, sem BYPASSRLS) e o login `sentinel_app_login` com a senha do `.env`. |
| `sentinel-migrate` | Execução única de `--migrate` usando o superusuário (`ConnectionStrings__DatabaseAdmin`). |
| `sentinel-api` | API conectada como `sentinel_app_login` (sujeita a RLS). Sobe só depois que `sentinel-migrate` termina com sucesso. |

- As portas ficam presas em `127.0.0.1` (5000 e 5432).
- O banco sobe **vazio** e **sem mocks**. Acesse `/review` para completar `/auth/setup` (informando a `ADMIN_API_KEY` em Production) ou provisione via `POST /api/admin/workspaces`. Se quiser carregar fixtures de demonstração explicitamente:

```bash
docker compose exec -T postgres psql -U postgres -d sentinel_mcp -v ON_ERROR_STOP=1 < db/fixtures/001_seed_demo.sql
docker compose run --rm sentinel-migrate --rebuild-projections
```

  A fixture insere apenas fontes da verdade (workspaces, vínculos, fluxos, payloads, eventos); a reconstrução gera as projeções. Alternativa: `docker compose run --rm sentinel-migrate --seed-demo` aplica a fixture e reconstrói, mas **continua rodando** a API dentro do container avulso; encerre com Ctrl+C depois da mensagem de conclusão.

### Opções de linha de comando

Sempre separe os argumentos da aplicação com `--` no `dotnet run`. Na imagem Docker, passe-os direto ao entrypoint (por exemplo `command: ["--migrate"]`).

| Comando | Efeito |
| --- | --- |
| `dotnet run --project src/Sentinel.Api -- --migrate` | Só com `Sentinel:StorageProvider=PostgreSQL`. Aplica migrações pendentes de `db/migrations` com a conexão admin, sob lock consultivo; se aplicou a `003`, reconstrói as projeções. Depois **sai**. |
| `dotnet run --project src/Sentinel.Api -- --seed-demo` | Só quando solicitado explicitamente: no modo local popula fixtures em memória e sobe a API; no PostgreSQL aplica `db/fixtures`, reconstrói projeções e **sai** com código `0`. |
| `dotnet run --project src/Sentinel.Api -- --rebuild-projections [--workspace-id <uuid>]` | Reconstrói as projeções a partir dos eventos (todos os workspaces ou um) e **sai**. Usa `ConnectionStrings:Database`. |
| `dotnet run --no-build --no-launch-profile --project src/Sentinel.Api -- --stdio --token <chave_snt_ou_jwt>` | Executa o servidor MCP via entrada/saída padrão (STDIO) usando o SDK oficial; logs vão para `stderr` (`--no-launch-profile` evita ruído no `stdout`). Exige `--token` ou `SENTINEL_MCP_TOKEN`. |

`Sentinel:AutoMigrate=true` migra na subida da API, mas exige que o processo da API tenha a conexão admin; não é recomendado em produção. Para automações administrativas existem os endpoints autenticados em `/api/admin/*`.

---

## Configuração e segredos

Nenhuma connection string, senha, client secret ou chave fica nos `appsettings*.json`. Forneça-os por variável de ambiente (separador `__`, por exemplo `ConnectionStrings__Database`) ou por um cofre de segredos.

| Chave | Padrão | Descrição |
| --- | --- | --- |
| `Sentinel:StorageProvider` | `InMemory` (`PostgreSQL` em `appsettings.Production.json`) | `InMemory` ou `PostgreSQL`. |
| `Sentinel:LocalDataFile` | vazio (`data/sentinel-local.json` em Development) | Arquivo JSON atômico para persistência local no provedor `InMemory` (reconstruído via redutores na inicialização). |
| `Sentinel:AllowInMemoryInProduction` | `false` | Permite InMemory em Production (não recomendado). |
| `Sentinel:SeedDemoData` | `false` (`false` em Development) | Popula fixtures apenas quando ativado explicitamente ou via `--seed-demo`. |
| `ConnectionStrings:Database` | — | Login de **menor privilégio** (membro de `sentinel_app`, sem superusuário/BYPASSRLS). Obrigatória com PostgreSQL, inclusive para `--migrate`. |
| `ConnectionStrings:DatabaseAdmin` | usa `Database` | Conexão para DDL, migrações e fixtures. |
| `Sentinel:AutoMigrate` | `false` | Migra ao iniciar a API. |
| `Sentinel:Database:AllowPrivilegedRole` | `false` | Em Production, permite iniciar mesmo se o login da API ignora RLS (não recomendado). |
| `Sentinel:AdminApiKey` / `ADMIN_API_KEY` | vazio | Chave administrativa para `/api/admin/workspaces` (e exigida em `/auth/setup` quando em Production). |
| `Sentinel:AllowMockTokens` | `false` (`false` em Development) | Aceita tokens `mock:` apenas se habilitado explicitamente em testes fora de Production. |
| `Sentinel:Web:EnableDevLogin` | `false` (`false` em Development) | Modo legado de login sem senha apenas para suítes de teste (sempre ignorado em Production). |
| `Sentinel:Web:DevLoginIssuer` | `https://auth.sentinel.dev` | Emissor reservado ao modo de teste legado quando habilitado. |
| `Sentinel:ApiKeys` | `[]` | Chaves de API estáticas em configuração (além das chaves dinâmicas `snt_...` criadas via console/API). |
| `Sentinel:Mcp:AllowedOrigins` | `[]` | Origens aceitas no cabeçalho `Origin` do `/mcp`, além da própria. |
| `Authentication:Mcp:Authority` | vazio | Emissor dos JWT de agentes. Vazio desabilita JWT e o endpoint `/.well-known/oauth-protected-resource`. |
| `Authentication:Mcp:Audience` | `sentinel-mcp` | Audiência exigida no JWT. |
| `Authentication:Web:Authority` / `ClientId` / `ClientSecret` | vazio | OIDC da página de revisão. `ClientSecret` só por cofre/variável de ambiente. |
| `Authentication:Web:Issuer` | `Authority` | Emissor usado no vínculo quando o token OIDC não traz `iss`. |
| `Sentinel:RateLimit:McpPerMinute` / `WebPerMinute` / `LoginPerMinute` | `300` / `120` / `20` | Limites por janela de 1 minuto. |
| `Sentinel:Retention:Enabled` / `ReceiptRetentionDays` / `IntervalMinutes` | `true` / `30` / `360` | Job que expurga `command_receipts` e `context_receipts` antigos. |

### Chaves de API de agentes (`snt_...` dinâmicas e estáticas)

O método recomendado é gerar chaves diretamente no console `/review` (**Conectar IAs (MCP)**) ou via `POST /api/admin/workspaces/{workspaceId}/api-keys`: o Sentinel cria uma chave CSPRNG com prefixo `snt_`, garante o vínculo ativo no workspace e armazena apenas o hash SHA-256 em banco/arquivo local (`agent_api_keys`). O segredo em claro é mostrado uma única vez e pode ser revogado a qualquer momento.

Também é possível declarar chaves estáticas em `Sentinel:ApiKeys` via variáveis de ambiente (comparadas em tempo constante pelo hash SHA-256):

```powershell
$chave = "snt_" + [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)).ToLowerInvariant()
$hash  = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($chave))).ToLowerInvariant()
$hash   # vai para a configuração; guarde $chave no cofre e entregue ao agente
```

```bash
CHAVE="snt_$(openssl rand -hex 32)"
printf '%s' "$CHAVE" | sha256sum | cut -d' ' -f1   # hash para a configuração
```

Configuração estática (por variáveis de ambiente, uma entrada por índice):

```bash
Sentinel__ApiKeys__0__Nome=agente-ci
Sentinel__ApiKeys__0__KeySha256=<hash hex>
Sentinel__ApiKeys__0__Subject=agent-ci
Sentinel__ApiKeys__0__Issuer=https://sentinel.local/api-keys
Sentinel__ApiKeys__0__ClientId=ci-pipeline
Sentinel__ApiKeys__0__Scopes__0=read:memory
Sentinel__ApiKeys__0__Scopes__1=write:incident
```

`Subject` e `Issuer` precisam corresponder a um vínculo ativo em `memberships` do workspace; o papel vem desse vínculo. O agente envia `Authorization: Bearer <chave>`.

---

## Segurança

### Credenciais de agente (`/mcp`)

Toda requisição com `id` exige `Authorization: Bearer`. Sem credencial válida a resposta é **401** com `WWW-Authenticate` (inclui `resource_metadata` quando JWT está configurado). Ordem de verificação:

1. **Chave de API** dinâmica por workspace (`snt_...` armazenada por hash SHA-256 em `agent_api_keys`) ou estática em `Sentinel:ApiKeys` (comparação em tempo constante).
2. **JWT**, quando `Authentication:Mcp:Authority` está configurado: valida emissor, audiência, assinatura e expiração. Escopos vêm das claims `scope`/`scp`; a identidade é `iss` + `sub`; `client_id`/`azp` são registrados como cliente.
3. **Token mock** `mock:...`, desabilitado por padrão (inclusive em Development) e aceito somente quando `Sentinel:AllowMockTokens=true` em testes automatizados fora de Production.

Escopos: `read:memory`, `write:decision`, `write:incident`. O **papel** (Reader, Contributor, Maintainer) vem do vínculo em `memberships`, não do token. Sem vínculo no workspace: `NOT_FOUND`. Papel ou escopo insuficiente: `FORBIDDEN`.

O servidor atua apenas como *resource server*: expõe `/.well-known/oauth-protected-resource` (RFC 9728) quando JWT está configurado, mas não implementa autorização OAuth, PKCE ou emissão de tokens para agentes.

### Endpoint `/mcp`

- Modo sem sessão: só `POST`. `GET` e `DELETE` retornam **405**.
- Versões suportadas: `2025-11-25`, `2025-06-18`, `2025-03-26`, `2024-11-05`. No `initialize`, a versão pedida é ecoada se suportada; senão, o servidor oferece a mais recente. Cabeçalho `MCP-Protocol-Version` não suportado retorna 400.
- Notificações (sem `id`) recebem **202** sem corpo. Lotes JSON-RPC não são suportados.
- Cabeçalho `Origin`, quando presente, precisa ser a própria origem ou constar em `Sentinel:Mcp:AllowedOrigins`; senão **403**.

### Revisão humana (`/review`) e Onboarding (`/auth/setup`)

- `GET /review` (e `/app`) exige sessão web autenticada (cookie `sentinel_session`, HttpOnly, 8 h com renovação). Se ainda não existir nenhum workspace no sistema, redireciona para **`/auth/setup`** para cadastrar o administrador local (hash PBKDF2-SHA256, 210 mil iterações), o primeiro workspace e o primeiro projeto; caso já exista, redireciona para `/auth/login`.
- Login: autenticação local real (`GET /auth/login` → `POST /auth/login` com usuário/e-mail e senha PBKDF2-SHA256) e/ou OIDC (authorization code + PKCE, `Authentication:Web:*`, callback `/auth/callback`). Logout: `POST /auth/logout`.
- Dados: `GET /api/review/state?workspaceId=` mostra apenas workspaces em que a identidade tem vínculo ativo (mais o bloco `sistema` com status de armazenamento e OIDC).
- Ações: `POST /api/review/approve`, `/reject` e `/restore` exigem cookie, cabeçalho `X-CSRF-Token` (antiforgery do ASP.NET Core) e papel Maintainer. Qualquer cabeçalho `Authorization` nessas rotas resulta em **403**. Rejeição exige motivo (1 a 1000 caracteres), gravado no evento. Restaurar cria uma nova proposta a partir de uma versão anterior, que ainda precisa ser aprovada.
- Exclusões destrutivas (remover projeto sem histórico, revogar chave de API ou remover vínculo) exigem confirmação explícita por digitação do identificador alvo no diálogo modal.

### Banco de dados

- Cada operação roda numa transação com `set_config('app.current_workspace_id', ..., true)` (e `app.current_key_hash` na validação de chaves de API).
- A migração `003` aplica `FORCE ROW LEVEL SECURITY`, cria o papel `sentinel_app` (NOLOGIN, sem BYPASSRLS) com GRANTs mínimos, torna `memory_events`/`memory_payloads` append-only por trigger e troca as FKs de eventos/payloads para `ON DELETE RESTRICT`.
- A migração `005` protege `memory_events.project_id` com `ON DELETE RESTRICT` e cria `local_users` e `agent_api_keys` (somente hash SHA-256) sob `FORCE ROW LEVEL SECURITY`.
- Em Production a API recusa iniciar se o login de `ConnectionStrings:Database` for superusuário ou tiver BYPASSRLS (salvo `Sentinel:Database:AllowPrivilegedRole=true`); fora de Production, apenas registra um aviso.

### Entrada e resposta

- Evidências: no máximo 10 por registro, URL HTTPS absoluta, sem credenciais embutidas (`user:pass@`) e sem parâmetros como `token=`, `secret=`, `password=`, `key=`, `api_key=`, `access_token=`.
- Limites de tamanho em título, resumo, justificativa, chaves técnicas, consulta e chave de idempotência (`ValidadorDeInvariantes`).
- Comprovantes não armazenam o texto da consulta, o prompt ou a conversa.
- Cabeçalhos: CSP sem script inline (`script-src 'self'`), `X-Frame-Options: DENY`, `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, HSTS fora de Development.
- Handler global de exceções devolve `INTERNAL_ERROR` com ID de correlação, sem detalhes internos.

---

## Operação

- `GET /healthz`: liveness (`{"status":"Healthy"}`).
- `GET /readyz`: verifica o armazenamento; 503 (`NotReady`) se o banco falhar. A imagem `aspnet` não tem `curl`; use o probe do orquestrador.
- Logs via `ILogger`, em JSON fora de Development.
- Rate limit por credencial (hash do cabeçalho `Authorization`) ou IP; login limitado por IP.
- Retenção: job em segundo plano expurga `command_receipts` e `context_receipts` mais antigos que `Sentinel:Retention:ReceiptRetentionDays` (30 dias por padrão). Após o expurgo, uma retentativa com a mesma `idempotencyKey` é tratada como comando novo.
- Runbooks: [deploy e migrações](docs/runbooks/deployment-and-migrations.md) e [recuperação e reconstrução](docs/runbooks/disaster-recovery-and-rebuild.md).

---

## Testes

```bash
dotnet test Sentinel.sln                       # tudo
dotnet test tests/Sentinel.UnitTests           # domínio, redutores, validação, idempotência, casos de uso
dotnet test tests/Sentinel.ContractTests       # protocolo MCP e schemas das tools
dotnet test tests/Sentinel.IntegrationTests    # AC-01 a AC-08 por HTTP
```

- `tests/Sentinel.IntegrationTests` roda os mesmos critérios de aceite contra **InMemory** e contra **PostgreSQL 16 via Testcontainers** (Docker necessário). No PostgreSQL a aplicação conecta como um login sem privilégios (membro de `sentinel_app`), e há testes específicos de RLS e de imutabilidade do log de eventos.
- Sem Docker, os testes de PostgreSQL são **ignorados**. Com `SENTINEL_REQUIRE_DOCKER=1` (definido no CI) eles **falham** em vez de serem ignorados.
- O CI (`.github/workflows/ci.yml`) compila com `-warnaserror` e executa as três suítes no `ubuntu-latest`.

---

## Critérios de aceite (AC-01 a AC-08)

Implementados em `tests/Sentinel.IntegrationTests/CriteriosDeAceite.cs`. O resultado de cada execução está no CI; este README não mantém um placar estático.

| Critério | O que o teste verifica |
| --- | --- |
| **AC-01** Interoperabilidade | `initialize`, `tools/list` e chamada de tool via HTTP. |
| **AC-02** Concorrência no mesmo tópico | 10 propostas concorrentes no mesmo tópico: 1 sucesso e 9 conflitos controlados. |
| **AC-03** Independência entre tópicos | 10 tópicos distintos em paralelo, todos com sucesso. |
| **AC-04** Idempotência | Retentativas sequenciais e concorrentes devolvem o resultado original; reuso da chave com argumentos diferentes retorna `IDEMPOTENCY_KEY_REUSED`. |
| **AC-05** Isolamento e revisão | `NOT_FOUND` entre workspaces; rotas de revisão recusam token MCP e sessão forjada; painel mostra só workspaces do usuário; evidência com esquema perigoso é recusada. |
| **AC-06** Vigência e histórico | Propor, aprovar, substituir; linha do tempo e comprovante preservam a versão anterior. |
| **AC-07** Qualidade e latência | Chave técnica exata vem primeiro; p95 de 20 consultas abaixo de 500 ms, medido ponta a ponta via HTTP no servidor de teste (`WebApplicationFactory`). |
| **AC-08** Recuperação | Reconstrução das projeções reproduz o mesmo estado. |

---

## Estrutura da solução

```
.
├── src/
│   ├── Sentinel.Core/
│   │   ├── CasosDeUso/        # ServicoDeMemoria: camada única de casos de uso
│   │   ├── Comandos/          # Comandos de escrita
│   │   ├── Comum/             # Relógio, hash PBKDF2-SHA256 e gerador snt_ (SegurancaLocal)
│   │   ├── Consultas/         # Modelos de consulta e resultado
│   │   ├── Contratos/         # Interfaces de repositório (IRepositorios.cs)
│   │   ├── Excecoes/          # Exceções de domínio com códigos de erro estáveis
│   │   ├── Modelos/           # Entidades, enumeradores e evidência
│   │   ├── Redutores/         # RedutorDeDecisao e RedutorDeIncidente
│   │   └── Validacao/         # ValidadorDeInvariantes
│   ├── Sentinel.Persistence/
│   │   ├── Comum/             # Ajudante de idempotência (hash de argumentos)
│   │   ├── EmMemoria/         # Repositório local em memória + persistência JSON atômica
│   │   └── PostgreSql/        # Repositório Npgsql e migrador
│   └── Sentinel.Api/
│       ├── Admin/             # API REST /api/admin/* para workspaces, projetos, chaves snt_ e usuários
│       ├── Auth/              # Identidade de agentes (chaves SHA-256 snt_/estáticas, JWT e mock só em testes)
│       ├── Mcp/               # Endpoint /mcp (JSON-RPC e ModelContextProtocol), dispatcher e schemas das tools
│       ├── Operacao/          # Diagnósticos OpenTelemetry e job de retenção
│       ├── WebReview/         # Onboarding (/auth/setup), login local/OIDC e revisão humana (/review)
│       ├── wwwroot/           # Landing, CSS e JS (sem script inline)
│       └── Program.cs         # Composição, CLI, autenticação e pipeline HTTP
├── tests/
│   ├── Sentinel.UnitTests/
│   ├── Sentinel.ContractTests/
│   └── Sentinel.IntegrationTests/ # Inclui FluxoSemMocksTests, AdminApiTests e AC-01..AC-08
├── db/
│   ├── migrations/            # 001..004 + 005 local_users, agent_api_keys e ON DELETE RESTRICT em eventos
│   ├── fixtures/              # Dados opcionais de demonstração para --seed-demo / Testcontainers
│   └── docker/                # Script de criação do papel/login no compose
├── docs/
│   ├── adr/                   # Decisões de arquitetura
│   ├── design/SDD.md          # Documento de design
│   ├── guides/                # Guias detalhados de arquitetura e conexão (Codex, Claude, Cursor, Gemini, VS Code)
│   └── runbooks/              # Deploy, migrações e recuperação
├── compose.yaml
├── Dockerfile
├── .env.example
└── AGENTS.md
```

### Página de revisão (`/review`) e Landing (`/`)

O console web em `/review` (`src/Sentinel.Api/WebReview/review.html` + `wwwroot/js/`) começa sem dados fictícios pelo onboarding `/auth/setup` e oferece governança humana completa:
- **Navegação Clara e Controles de Fechamento**: barra lateral colapsável e retrátil (`Menu` / `×`), Inspetor em gaveta responsiva com botão `× Fechar`, barra `← Voltar` / `Visão Geral` nos detalhes e visão inicial explicativa do fluxo em 4 etapas.
- **Fila de Revisão com Diff Lado a Lado**: comparação direta entre a **Decisão Vigente** e a **Proposta Pendente** (título, resumo, justificativa, chaves técnicas e evidências HTTPS).
- **Catálogo de Decisões, Histórico e Restauração**: filtros por projeto e busca instantânea, histórico de versões (`GET /api/review/timeline`) e botão de restauração (`POST /api/review/restore`) para propor reversão segura.
- **Incidentes e Eventos Append-Only**: filtro por tipo de evento e inspeção cronológica.
- **Hub de Integração MCP e Auditor de Recibos**: geração direta de chaves reais `snt_...` por workspace (mostradas uma vez) com blocos prontos para **OpenAI Codex**, Claude Code, Cursor, Gemini CLI e VS Code Copilot, além de verificação SHA-256 (`GET /api/review/receipt`).
- **Administração Protegida por Confirmação**: criação e exclusão de projetos (bloqueada quando há histórico imutável), usuários locais (PBKDF2-SHA256) e vínculos, exigindo digitação exata do identificador alvo antes de confirmar qualquer remoção ou revogação.

Os scripts e folhas de estilo são servidos de `wwwroot/js/` e `wwwroot/css/`, com CSP estrita (`script-src 'self'`), paleta de comandos (`Ctrl+K` / `⌘K` com botão `×`) e tema claro/escuro.

---

## Recursos Administrativos e Observabilidade

- **API de Administração (`/api/admin/*`)**: Gestão de workspaces, projetos (`GET/POST/DELETE .../projects`), chaves de API (`GET/POST/DELETE .../api-keys`), usuários locais (`GET/POST .../users`) e vínculos (`GET/POST/DELETE .../memberships`). Criação de workspace exige `X-Admin-Key` / `ADMIN_API_KEY`; gestão interna aceita `X-Admin-Key` ou sessão web de `Maintainer` com CSRF.
- **OpenTelemetry**: Tracing distribuído e métricas integradas para ASP.NET Core, HttpClient, Runtime e contadores de chamadas/erros de tools MCP (`sentinel_mcp_tool_calls_total`, `sentinel_mcp_tool_errors_total`). Exportação OTLP via `OTEL_EXPORTER_OTLP_ENDPOINT` ou `Sentinel:OpenTelemetry:OtlpEndpoint`.
- **SDK MCP, STDIO e Schemas**: Integração com `ModelContextProtocol.AspNetCore` (`/mcp/sse` e `--stdio`), endpoint stateless `POST /mcp`, negociação de versões `2025-11-25` a `2024-11-05`, `outputSchema`, `annotations`, `resources/*` e `prompts/*`.
- **Segurança de Conteúdo (CSP)**: CSP estrita aplicada globalmente a todas as páginas HTML servidas, sem exceções de scripts inline ou bibliotecas de terceiros via CDN.

---

## Limitações conhecidas e pendências

- **Sem emissor OAuth próprio**: o Sentinel valida JWT de provedores OIDC terceiros, mas não atua como Authorization Server nem emite tokens.
- **Busca sem `unaccent`**: termos com e sem acento podem não se casar (o dicionário `portuguese` não remove acentos). Não há busca vetorial.
- **Linha do tempo pagina em memória**: `get_timeline` carrega todos os eventos do fluxo e pagina na aplicação; fluxos muito longos custam proporcionalmente.
- **Migrações só para frente**: não há scripts de reversão; o rollback é por restauração de backup (ver runbook).

---

## Documentação complementar

- [AGENTS.md](AGENTS.md): comandos para agentes e desenvolvedores.
- [docs/guides/conectando-agentes-mcp.md](docs/guides/conectando-agentes-mcp.md): guia prático para **OpenAI Codex**, Claude Code, Cursor, Gemini CLI e VS Code.
- [docs/guides/arquitetura-e-fluxos.md](docs/guides/arquitetura-e-fluxos.md): explicação detalhada do que tem no sistema e como cada módulo funciona.
- [docs/adr](docs/adr): decisões de arquitetura (cada ADR traz uma nota de estado da implementação quando diverge do código).
- [docs/design/SDD.md](docs/design/SDD.md): documento de design.
- [docs/runbooks](docs/runbooks): deploy, migrações, recuperação e reconstrução.

---

## Licença

Distribuído sob a licença **MIT**. Veja [LICENSE](LICENSE).
