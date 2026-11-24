# Sentinel-MCP — Guia para Agentes de IA e Engenharia

Este documento instrui agentes autônomos e desenvolvedores sobre o funcionamento, comandos e execução repetível do Sentinel-MCP. Detalhes de segurança e configuração estão no [README](README.md).

## Pré-requisitos
- .NET 9.0 SDK (`dotnet --version`)
- Docker: necessário para `docker compose` e para os testes de integração contra PostgreSQL (Testcontainers)
- PostgreSQL 16+ apenas se for rodar a API com `Sentinel:StorageProvider=PostgreSQL` fora do compose

## Estrutura da Solução
- `src/Sentinel.Core`: domínio sem dependência de infraestrutura.
  - `CasosDeUso/ServicoDeMemoria.cs`: camada única de casos de uso (validação de entrada, vínculo/papel no workspace, regra de canal web para aprovar/rejeitar/restaurar, setup inicial, autenticação local PBKDF2, gestão de chaves de API `snt_` e proteção contra exclusão de projetos com histórico). MCP e página web chamam apenas esta classe.
  - `Contratos/IRepositorios.cs`: interfaces de repositório, usuários locais, chaves de API, reconstrução de projeções, retenção e prontidão.
  - `Redutores/`: `RedutorDeDecisao` e `RedutorDeIncidente`, única fonte das regras de projeção.
  - `Validacao/ValidadorDeInvariantes.cs`, `Modelos/`, `Comandos/`, `Consultas/`, `Excecoes/` (códigos de erro estáveis), `Comum/` (`Relogio`, `SegurancaLocal`).
- `src/Sentinel.Persistence`: `EmMemoria/` (dev/testes com persistência atômica opcional via `Sentinel:LocalDataFile`) e `PostgreSql/` (Npgsql com SQL puro, RLS por transação, migrador com lock consultivo).
- `src/Sentinel.Api`: `Program.cs` (composição, CLI HTTP e `--stdio`, autenticação, pipeline), `Mcp/` (`POST /mcp` JSON-RPC stateless com negociação `2025-11-25`/`2025-06-18`/`2025-03-26`/`2024-11-05`, `/mcp/sse` via SDK oficial `ModelContextProtocol.AspNetCore`, `--stdio` para clientes CLI locais, `outputSchema`, `annotations`, `resources/*` e `prompts/*`), `Admin/` (API REST `/api/admin/*` para workspaces, projetos, chaves de API, usuários locais e vínculos), `Auth/` (chaves de API SHA-256 dinâmicas e estáticas, JWT e mock apenas em testes), `WebReview/` (`/auth/setup`, `/auth/login`, `/review` e `/api/review/*`), `Operacao/` (diagnósticos OpenTelemetry e job de retenção), `wwwroot/`.
- `db/migrations` (001 schema, 002 RLS, 003 segurança/busca/integridade, 004 grants administrativos de escrita, 005 contas locais + chaves de API SHA-256 + proteção de projetos), `db/fixtures` (dados opcionais para `--seed-demo` e Testcontainers), `db/docker` (criação do papel/login no compose).
- `tests/Sentinel.UnitTests`: invariantes, redutores, idempotência e `ServicoDeMemoria`.
- `tests/Sentinel.ContractTests`: protocolo MCP e schemas das tools.
- `tests/Sentinel.IntegrationTests`: AC-01 a AC-08 por HTTP, contra InMemory e contra PostgreSQL via Testcontainers (inclui testes de RLS e de imutabilidade do log).

## Ambiente
Há `src/Sentinel.Api/Properties/launchSettings.json` definindo `ASPNETCORE_ENVIRONMENT=Development` e `http://localhost:5000` para execução local com `dotnet run`; ao publicar binários ou usar `--no-launch-profile`, defina `ASPNETCORE_ENVIRONMENT=Development` explicitamente (sem ela o padrão é **Production**, que usa PostgreSQL). No modo local (`Development`), os dados são persistidos em `data/sentinel-local.json` via `Sentinel:LocalDataFile`, **sem dados mock**, **sem tokens `mock:`** e **sem login de demonstração** habilitados por padrão. Ao abrir `/review` pela primeira vez, `/auth/setup` guia a criação do administrador, do primeiro workspace e do primeiro projeto reais.

```bash
export ASPNETCORE_ENVIRONMENT=Development      # bash
```
```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"    # PowerShell
```

## Comandos Essenciais

### Compilar
```bash
dotnet build Sentinel.sln
```

### Testes
```bash
dotnet test Sentinel.sln --logger "console;verbosity=normal"
dotnet test tests/Sentinel.UnitTests
dotnet test tests/Sentinel.ContractTests
dotnet test tests/Sentinel.IntegrationTests
```
Os testes de PostgreSQL exigem Docker. Sem Docker eles são ignorados (aparecem como *skipped*); com `SENTINEL_REQUIRE_DOCKER=1` (usado no CI) eles falham. Não existe categoria `Smoke`.

### Executar a API
```bash
dotnet run --project src/Sentinel.Api          # http://localhost:5000
```

### Argumentos da aplicação (sempre depois de `--`)
```bash
# PostgreSQL: aplica migrações pendentes (conexão admin, lock consultivo); se aplicar a 003, reconstrói projeções; sai.
dotnet run --project src/Sentinel.Api -- --migrate

# InMemory: popula dados de demonstração e continua rodando a API.
# PostgreSQL: aplica db/fixtures (conexão admin), reconstrói projeções e sai com código 0 (ideal para automações/migrações).
dotnet run --project src/Sentinel.Api -- --seed-demo

# Reconstrói projeções a partir dos eventos (todos os workspaces ou um) e sai.
dotnet run --project src/Sentinel.Api -- --rebuild-projections
dotnet run --project src/Sentinel.Api -- --rebuild-projections --workspace-id <uuid>

# Executa como servidor MCP em STDIO (ideal para integração local com Codex / Claude Code sem porta HTTP).
# Gere uma chave snt_... no console /review -> Conectar IAs (MCP) ou informe via SENTINEL_MCP_TOKEN.
dotnet run --no-build --no-launch-profile --project src/Sentinel.Api -- --stdio --token "$env:SENTINEL_MCP_TOKEN"
```
`--migrate` só tem efeito com `Sentinel:StorageProvider=PostgreSQL` (com InMemory ele é ignorado e a API sobe normalmente). `--seed-demo` popula fixtures explícitas quando solicitado (nunca no arranque padrão). Não existe endpoint HTTP de manutenção.

### Ambiente com Docker Compose (PostgreSQL)
```bash
cp .env.example .env            # defina POSTGRES_PASSWORD, SENTINEL_APP_DB_PASSWORD e ADMIN_API_KEY
docker compose up -d --build    # postgres -> sentinel-migrate (--migrate) -> sentinel-api
```
O banco sobe vazio. Ao acessar `/review` pela primeira vez, execute `/auth/setup` para criar seu administrador local e workspace, ou use `POST /api/admin/workspaces` com `X-Admin-Key`. Para fixtures opcionais de demonstração:
```bash
docker compose exec -T postgres psql -U postgres -d sentinel_mcp -v ON_ERROR_STOP=1 < db/fixtures/001_seed_demo.sql
docker compose run --rm sentinel-migrate --rebuild-projections
```

## Configuração Inicial e Smoke Test Rápido (Sem Mocks)
1. Inicie a API com `ASPNETCORE_ENVIRONMENT=Development` (`dotnet run --project src/Sentinel.Api`). Os dados ficam persistidos em `src/Sentinel.Api/data/sentinel-local.json` (configurável por `Sentinel:LocalDataFile`).
2. Health check:
   ```bash
   curl http://localhost:5000/healthz
   # {"status":"Healthy"}
   ```
3. Acesse `http://localhost:5000/review` — se não houver workspaces ainda, você será levado para `/auth/setup` para cadastrar usuário administrador local (PBKDF2-SHA256), primeiro workspace e primeiro projeto.
4. No console `/review`, abra **Conectar IAs (MCP)** (ou `POST /api/admin/workspaces/{workspaceId}/api-keys`) e gere uma chave real com prefixo `snt_...`. Tokens `mock:...` ficam desabilitados por padrão (`AllowMockTokens=false` e `EnableDevLogin=false`), reservados apenas aos testes automatizados.
5. Handshake MCP com a chave real (`snt_...`):
   ```bash
   curl -X POST http://localhost:5000/mcp \
     -H "Content-Type: application/json" \
     -H "Authorization: Bearer snt_sua_chave_aqui" \
     -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"smoke","version":"1.0"}}}'
   ```
   Resposta esperada: `protocolVersion` `2025-11-25` (ou a versão negociada), `capabilities.tools`, `capabilities.resources`, `capabilities.prompts` e `serverInfo.name` `Sentinel-MCP`.
6. Liste as tools com `{"jsonrpc":"2.0","id":2,"method":"tools/list"}` (mesmo cabeçalho): `propose_decision`, `log_incident`, `query_context`, `get_timeline`, `get_context_receipt`, `list_workspaces`.
7. Teste descoberta automática por agente (ex.: OpenAI Codex) chamando `tools/call` com `{"name":"list_workspaces","arguments":{}}`.
8. Sem `Authorization` `/mcp` e `/mcp/sse` respondem `401` com `WWW-Authenticate`; `GET /mcp` responde `405`.

## API de Administração (`/api/admin/*`)
A API administrativa permite gerenciar workspaces, projetos, chaves de API, usuários locais e vínculos sem depender de scripts SQL diretos:
- `GET /api/admin/workspaces`: lista workspaces (`AdminSistema` lista todos; sessão web lista apenas os que o usuário integra).
- `POST /api/admin/workspaces`: cria um workspace (`{ "id": "uuid", "slug": "slug-valido", "nome": "Nome" }`) — exige `X-Admin-Key`.
- `GET /api/admin/workspaces/{workspaceId}`: obtém workspace, projetos e vínculos (`X-Admin-Key` ou `Maintainer` autenticado no workspace).
- `GET /api/admin/workspaces/{workspaceId}/projects`: lista os projetos do workspace (`X-Admin-Key` ou membro autenticado).
- `POST /api/admin/workspaces/{workspaceId}/projects`: cria um projeto associado (`{ "name": "Nome", "slug": "slug-valido" }`) — exige CSRF quando autenticado via cookie web.
- `DELETE /api/admin/workspaces/{workspaceId}/projects/{projectId}`: remove um projeto sem histórico (`409 Conflict` se já houver eventos, tópicos ou incidentes). Exige `Maintainer` + CSRF (ou `X-Admin-Key`).
- `GET /api/admin/workspaces/{workspaceId}/api-keys`: lista metadados e prefixos das chaves SHA-256 de agentes do workspace.
- `POST /api/admin/workspaces/{workspaceId}/api-keys`: gera nova chave `snt_...` com escopos (`read:memory`, `write:decision`, `write:incident`) e vínculo opcional automático (`role`). O segredo em claro só é retornado nesta resposta.
- `DELETE /api/admin/workspaces/{workspaceId}/api-keys/{keyId}`: revoga uma chave de API (`X-Admin-Key` ou `Maintainer` + CSRF).
- `GET /api/admin/workspaces/{workspaceId}/users`: lista usuários locais associados ao workspace.
- `POST /api/admin/workspaces/{workspaceId}/users`: cria usuário local com senha PBKDF2-SHA256 e vínculo inicial no workspace.
- `GET /api/admin/workspaces/{workspaceId}/memberships`: lista os vínculos do workspace.
- `POST /api/admin/workspaces/{workspaceId}/memberships`: cria ou atualiza um vínculo (`{ "subject": "agent-id", "issuer": "https://...", "role": "Contributor"|"Maintainer"|"Reader" }`).
- `DELETE /api/admin/workspaces/{workspaceId}/memberships/{vinculoId}`: remove um vínculo pelo identificador.

**Autenticação**: Requer o cabeçalho `X-Admin-Key: <ADMIN_API_KEY>` ou `Authorization: Bearer <ADMIN_API_KEY>` (configurado via variável de ambiente `ADMIN_API_KEY` ou `Sentinel:AdminApiKey`), ou sessão web de usuário com papel `Maintainer` no workspace (com `X-CSRF-Token` em mutações).

## API de Revisão Humana (`/api/review/*`) e Onboarding
- `GET/POST /auth/setup`: configuração inicial quando não existem workspaces ainda (cria usuário local PBKDF2, primeiro workspace e primeiro projeto; exige `ADMIN_API_KEY` em Production).
- `GET /auth/login` e `POST /auth/login`: autenticação local por e-mail/usuário e senha PBKDF2-SHA256 (ou OIDC/PKCE quando configurado).
- `GET /api/review/state`: lista workspaces, projetos, propostas pendentes, decisões vigentes, incidentes, eventos e metadados `sistema` do usuário web autenticado.
- `GET /api/review/timeline?workspaceId=&projectId=&kind=&streamId=&atVersion=&limit=`: consulta linha do tempo e eventos de uma decisão ou incidente.
- `GET /api/review/receipt?workspaceId=&projectId=&queryId=`: verifica integridade SHA-256 de um comprovante de consulta.
- `POST /api/review/approve` | `POST /api/review/reject` | `POST /api/review/restore`: mutações de governança que exigem sessão web de `Maintainer` e `X-CSRF-Token`.

## Observabilidade e OpenTelemetry
- Métricas e Rastreamento distribuído (Tracing) instrumentados via pacotes oficiais do OpenTelemetry (`OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Instrumentation.AspNetCore`, `Http`, `Runtime`).
- Ferramentas MCP instrumentadas com métricas de contador: `sentinel_mcp_tool_calls_total` e `sentinel_mcp_tool_errors_total`.
- Para exportar via OTLP (gRPC / HTTP), defina a variável `OTEL_EXPORTER_OTLP_ENDPOINT` ou `Sentinel:OpenTelemetry:OtlpEndpoint`.

## Regras para agentes que alteram este repositório
- Regras de negócio e autorização ficam em `ServicoDeMemoria`; não duplique validação nos canais (MCP/web).
- Projeções devem vir sempre dos redutores; não escreva em `decision_current`, `incident_current` ou `decision_pending_proposals` fora do caminho do redutor.
- `memory_events` e `memory_payloads` são append-only; mudanças de schema entram como nova migração numerada em `db/migrations` (não edite migrações já aplicadas).
- Não coloque connection strings, senhas, client secrets ou chaves de API em `appsettings*.json`, no `compose.yaml` ou em fixtures.
