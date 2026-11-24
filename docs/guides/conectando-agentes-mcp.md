# Guia de Conexão — Clientes e Modelos MCP (Codex, Claude, Cursor, Gemini, VS Code)

Este guia detalha como conectar **qualquer agente ou cliente compatível com MCP** ao **Sentinel-MCP**, cobrindo:
- **OpenAI Codex** (`codex-cli` e extensão Codex do VS Code — inclusive particularidades de variáveis de ambiente no Windows);
- **Claude Code**;
- **Cursor**;
- **Gemini CLI**;
- **VS Code Copilot (MCP)**.

---

## 1. Antes de conectar: escolha do transporte e autenticação

O Sentinel-MCP oferece **três formas de conexão MCP**:

| Transporte | Endpoint / Comando | Perfil recomendado | Protocolos negociados |
| --- | --- | --- | --- |
| **Streamable HTTP Stateless (JSON-RPC)** | `POST http://localhost:5000/mcp` | Uso geral em HTTP (`Codex`, `Claude Code`, `Cursor`, `Gemini CLI`, `VS Code`) | `2025-11-25`, `2025-06-18`, `2025-03-26`, `2024-11-05` |
| **SDK Oficial Streamable HTTP (Stateful)** | `http://localhost:5000/mcp/sse` | Clientes que utilizam o ciclo de sessão do `ModelContextProtocol.AspNetCore` | Negociado pelo SDK oficial C# (`2.0.0`) |
| **STDIO (CLI Local)** | `dotnet run --no-build --project src/Sentinel.Api -- --stdio` | Execução local sem precisar manter porta HTTP aberta ou quando a IDE bloqueia loopback/env | `WithStdioServerTransport()` do SDK oficial |

### Token de acesso
Em ambiente **`Development`** (`ASPNETCORE_ENVIRONMENT=Development`, `Sentinel:AllowMockTokens=true`), você pode usar tokens `mock:` para testar rapidamente as credenciais de demonstração:

- **Agente contribuidor no Workspace Demo (`11111111-...`)**:
  ```text
  mock:agent-dev-1|read:memory,write:decision,write:incident|codex
  ```
- **Agente somente leitura (`reader-bob`)**:
  ```text
  mock:reader-bob|read:memory|codex
  ```
- **Segundo workspace isolado (`22222222-...`, para validar isolamento multi-tenant)**:
  ```text
  mock:sec-agent-1|read:memory,write:decision,write:incident|codex
  ```

> **Produção (`ASPNETCORE_ENVIRONMENT=Production`)**: tokens `mock:` são recusados mesmo que alguém tente habilitá-los fora de ambientes não produtivos. Utilize **JWT Bearer emitido pelo seu provedor OIDC** (`Authentication:Mcp:Authority` + `Audience`) ou **Chave de API SHA-256** configurada em `Sentinel:ApiKeys` (injetada via variável de ambiente/cofre). Nunca armazene tokens reais no repositório.

### Suba o servidor localmente (modo Development / InMemory)
No diretório raiz do repositório:

```powershell
# PowerShell (Windows)
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet run --project src/Sentinel.Api
```

```bash
# Bash (Linux / macOS / WSL)
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Sentinel.Api
```

A API sobe em `http://localhost:5000`:
- Interface web e Console de Revisão: `http://localhost:5000/` e `http://localhost:5000/review`
- Health / Readiness: `http://localhost:5000/healthz` e `http://localhost:5000/readyz`
- Endpoint MCP principal: `http://localhost:5000/mcp`

> **Dica rápida no Console Web**: após autenticar-se em `/review`, acesse a aba **Integração MCP** para copiar esses trechos prontos e validar recibos ao vivo.

---

## 2. Conectando no OpenAI Codex (`codex-cli` e extensão VS Code)

### Opção A — Via CLI (`codex mcp add`) com HTTP
```powershell
# 1. Exporte o token no shell atual antes de abrir o Codex
$env:SENTINEL_MCP_TOKEN = "mock:agent-dev-1|read:memory,write:decision,write:incident|codex"

# 2. Registre o servidor HTTP
codex mcp add sentinel --url http://localhost:5000/mcp --bearer-token-env-var SENTINEL_MCP_TOKEN

# 3. Verifique o registro
codex mcp get sentinel
```

> **Nota importante para Windows + Extensão Codex do VS Code / ChatGPT Desktop**:
> Extensões gráficas no Windows nem sempre herdam variáveis de ambiente definidas em sessões isoladas do PowerShell, o que faz `bearer_token_env_var` enviar requisição sem `Authorization` (recebendo `401 Unauthorized`). Nesse caso, configure o header explicitamente no arquivo `~/.codex/config.toml` (ou `%USERPROFILE%\.codex\config.toml`), conforme a **Opção B** abaixo.

### Opção B — `~/.codex/config.toml` com header explícito (HTTP)
Edite `%USERPROFILE%\.codex\config.toml` (Linux/macOS: `~/.codex/config.toml`):

```toml
[mcp_servers.sentinel]
url = "http://localhost:5000/mcp"

[mcp_servers.sentinel.http_headers]
Authorization = "Bearer mock:agent-dev-1|read:memory,write:decision,write:incident|codex"
```

### Opção C — `~/.codex/config.toml` via STDIO (não depende de porta HTTP)
Compile o projeto antes uma vez (`dotnet build Sentinel.sln`) para o startup via `--no-build` ser instantâneo:

```toml
[mcp_servers.sentinel]
command = "dotnet"
args = [
  "run",
  "--no-build",
  "--no-launch-profile",
  "--project",
  "C:/caminho/para/SentinelMCP-main/src/Sentinel.Api",
  "--",
  "--stdio",
  "--token",
  "mock:agent-dev-1|read:memory,write:decision,write:incident|codex"
]

[mcp_servers.sentinel.env]
ASPNETCORE_ENVIRONMENT = "Development"
```

> **Atenção no modo STDIO com `InMemory`**: cada execução `--stdio` em modo `InMemory` inicia um processo isolado com os dados de demonstração carregados na memória daquele processo. Para compartilhar estado persistente entre múltiplos clientes (`Codex` + painel web `/review` em `http://localhost:5000`), use o servidor HTTP (`POST /mcp`) ou configure `--stdio` apontando para `PostgreSQL` (`Sentinel:StorageProvider=PostgreSQL`).

### Primeiro comando recomendado no Codex
Depois de reiniciar o Codex / recarregar o painel MCP, peça ao modelo:
> *"Chame a tool `list_workspaces` do Sentinel MCP para descobrir meu workspaceId e projectId, depois use `query_context` para buscar decisões sobre `idempotência` ou `BillingService`."*

---

## 3. Conectando no Claude Code

```bash
claude mcp add --transport http sentinel http://localhost:5000/mcp \
  -H "Authorization: Bearer mock:agent-dev-1|read:memory,write:decision,write:incident|claude-code"
```

Ou usando o servidor via **STDIO**:
```bash
claude mcp add sentinel -e ASPNETCORE_ENVIRONMENT=Development -- \
  dotnet run --no-build --no-launch-profile --project ./src/Sentinel.Api -- --stdio \
  --token "mock:agent-dev-1|read:memory,write:decision,write:incident|claude-code"
```

---

## 4. Conectando no Cursor

Crie ou edite `.cursor/mcp.json` no projeto (ou `~/.cursor/mcp.json`):

```json
{
  "mcpServers": {
    "sentinel": {
      "url": "http://localhost:5000/mcp",
      "headers": {
        "Authorization": "Bearer mock:agent-dev-1|read:memory,write:decision,write:incident|cursor"
      }
    }
  }
}
```

---

## 5. Conectando no Gemini CLI

Adicione em `~/.gemini/settings.json` (ou `.gemini/settings.json` do workspace):

```json
{
  "mcpServers": {
    "sentinel": {
      "httpUrl": "http://localhost:5000/mcp",
      "headers": {
        "Authorization": "Bearer mock:agent-dev-1|read:memory,write:decision,write:incident|gemini-cli"
      }
    }
  }
}
```

---

## 6. Conectando no VS Code (GitHub Copilot / MCP Nativo)

Crie `.vscode/mcp.json`:

```json
{
  "inputs": [
    {
      "type": "promptString",
      "id": "sentinel-token",
      "description": "Token Bearer do agente no Sentinel-MCP",
      "password": true
    }
  ],
  "servers": {
    "sentinel": {
      "type": "http",
      "url": "http://localhost:5000/mcp",
      "headers": {
        "Authorization": "Bearer ${input:sentinel-token}"
      }
    }
  }
}
```

---

## 7. Validação rápida via linha de comando (Smoke Test)

Com o servidor rodando em `http://localhost:5000`:

### 7.1. Handshake `initialize` (versão `2025-11-25`)
```powershell
$headers = @{
  "Content-Type"  = "application/json"
  "Authorization" = "Bearer mock:agent-dev-1|read:memory,write:decision,write:incident|codex"
}

Invoke-RestMethod -Method Post -Uri "http://localhost:5000/mcp" -Headers $headers -Body '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"codex-cli","version":"1.0"}}}' | ConvertTo-Json -Depth 10
```

### 7.2. Descobrir tools (`tools/list`) e workspaces (`list_workspaces`)
```powershell
# Listar as 6 ferramentas com inputSchema, outputSchema e annotations
Invoke-RestMethod -Method Post -Uri "http://localhost:5000/mcp" -Headers $headers -Body '{"jsonrpc":"2.0","id":2,"method":"tools/list"}' | ConvertTo-Json -Depth 10

# Descobrir automaticamente workspaces e projetos da credencial
Invoke-RestMethod -Method Post -Uri "http://localhost:5000/mcp" -Headers $headers -Body '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"list_workspaces","arguments":{}}}' | ConvertTo-Json -Depth 10
```

### 7.3. Consultar decisões e ler Resources / Prompts
```powershell
# Resources publicados
Invoke-RestMethod -Method Post -Uri "http://localhost:5000/mcp" -Headers $headers -Body '{"jsonrpc":"2.0","id":4,"method":"resources/read","params":{"uri":"sentinel://workspaces"}}' | ConvertTo-Json -Depth 10

# Prompts de governança
Invoke-RestMethod -Method Post -Uri "http://localhost:5000/mcp" -Headers $headers -Body '{"jsonrpc":"2.0","id":5,"method":"prompts/get","params":{"name":"consult_context_before_change","arguments":{"topic":"BillingService"}}}' | ConvertTo-Json -Depth 10
```

---

## 8. Diagnóstico de problemas comuns

| Sintoma | Causa provável | Como resolver |
| --- | --- | --- |
| `401 Unauthorized` + `WWW-Authenticate` | Token ausente ou malformado, ou ambiente rodando em `Production` usando `mock:` | Verifique `ASPNETCORE_ENVIRONMENT=Development`; no Windows/Codex IDE use `[mcp_servers.sentinel.http_headers]` ou `--stdio`. |
| `403 Forbidden` ou `NOT_FOUND` para workspace | Agente autenticado não possui vínculo (`membership`) ativo no workspace ou falta escopo (`write:decision` / `write:incident`) | Abra `/review` -> aba **Administração** para vincular o `subject` + `issuer` como `Contributor`, ou confira os escopos no token. |
| `INVALID_ORIGIN` (`403`) | Cliente enviou header `Origin` de navegador externo não listado em `Sentinel:Mcp:AllowedOrigins` | Clientes CLI não devem enviar `Origin` de site terceiro; para apps web autorizados, cadastre a origem em `Sentinel:Mcp:AllowedOrigins`. |
| Agente não sabe o `workspaceId` | Modelo tentou chamar `query_context` antes de descobrir os IDs | Peça ao agente para executar `list_workspaces` (ou ler `sentinel://workspaces`) primeiro. |
| `PROPOSAL_PENDING` (`409`) | Já existe proposta aguardando aprovação humana para o mesmo `topicKey` | Aprove ou rejeite a proposta pendente em `http://localhost:5000/review` antes de submeter nova alteração. |
