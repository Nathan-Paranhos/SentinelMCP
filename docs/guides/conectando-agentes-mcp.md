# Guia de Conexão — Conectando IAs e Clientes MCP (Sem Mocks)

Este guia explica passo a passo como **rodar o Sentinel-MCP**, gerar **chaves de API reais (`snt_...`, hash SHA-256)** e conectar os principais assistentes e modelos de IA:
- **OpenAI Codex** (`codex-cli` e extensão Codex do VS Code — incluindo configuração recomendada no Windows);
- **Claude Code**;
- **Cursor IDE**;
- **Gemini CLI**;
- **VS Code Copilot (MCP)**.

---

## 1. Suba o servidor e faça a configuração inicial (sem dados mock)

Por padrão, o ambiente **`Development`** inicia **100% limpo**, sem workspaces fictícios nem tokens mock, persistindo tudo o que você criar em `src/Sentinel.Api/data/sentinel-local.json` (arquivo local ignorado pelo Git).

```powershell
# PowerShell (Windows) — na raiz do repositório
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet run --project src/Sentinel.Api
```

```bash
# Bash (Linux / macOS / WSL)
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Sentinel.Api
```

1. Abra **`http://localhost:5000/review`** (ou clique em **Abrir Sentinel Workspace** na página inicial `http://localhost:5000/`).
2. No primeiro acesso, você será levado para **`/auth/setup`**:
   - Preencha **Nome/Slug do Workspace**, **Primeiro Projeto**, **Usuário Administrador** e **Senha** (mínimo 8 caracteres, protegida com PBKDF2-SHA256).
3. Ao entrar no console `/review`:
   - Abra no menu lateral **3. Auditoria & IAs (MCP) → Conectar IAs (MCP)** (ou **4. Administração → Projetos, Chaves & Acesso**).
   - No formulário **Gerar Chave de API Real**, escolha o cliente (ex.: `OpenAI Codex`), informe um identificador de agente (ex.: `codex-local`) e clique em **Gerar Chave Agora**.
   - **Copie a chave `snt_...` exibida no quadro verde** (por segurança ela aparece uma única vez; o servidor guarda apenas o hash SHA-256 e garante automaticamente o vínculo do agente no workspace).

> **Em produção (`ASPNETCORE_ENVIRONMENT=Production`)**: recomenda-se `Sentinel:StorageProvider=PostgreSQL`, OIDC + PKCE em `/auth/login` e chaves de API `snt_...` (gerenciadas no console com migração `005`) ou tokens **JWT Bearer** validados por `Authentication:Mcp:Authority`.

---

## 2. Escolha do transporte MCP

| Transporte | Endpoint / Comando | Quando usar | Versões de protocolo |
| --- | --- | --- | --- |
| **Streamable HTTP Stateless (JSON-RPC)** | `POST http://localhost:5000/mcp` | **Recomendado.** Compartilha a mesma memória/banco em tempo real com o painel `/review`. | `2025-11-25`, `2025-06-18`, `2025-03-26`, `2024-11-05` |
| **SDK Oficial Streamable HTTP (Stateful)** | `http://localhost:5000/mcp/sse` | Clientes que utilizam ciclo de sessão stateful do `ModelContextProtocol.AspNetCore`. | Negociado pelo SDK C# (`2.0.0`) |
| **STDIO (CLI Local)** | `dotnet run --no-build --no-launch-profile --project src/Sentinel.Api -- --stdio` | Quando você não quer deixar um servidor HTTP rodando (requer compilar antes com `dotnet build`). | `WithStdioServerTransport()` do SDK oficial |

---

## 3. Conectando no OpenAI Codex (`codex-cli` e extensão VS Code)

### Opção A — `~/.codex/config.toml` com header explícito (Recomendada no Windows + VS Code)
No Windows, extensões do VS Code nem sempre herdam variáveis de ambiente do terminal. Declarar `[mcp_servers.sentinel.http_headers]` diretamente em `%USERPROFILE%\.codex\config.toml` (Linux/macOS: `~/.codex/config.toml`) evita erros `401 Unauthorized`:

```toml
[mcp_servers.sentinel]
url = "http://localhost:5000/mcp"

[mcp_servers.sentinel.http_headers]
Authorization = "Bearer snt_COLE_SUA_CHAVE_AQUI"
```

### Opção B — Via `codex-cli` (HTTP com variável de ambiente)
```powershell
$env:SENTINEL_MCP_TOKEN = "snt_COLE_SUA_CHAVE_AQUI"
codex mcp add sentinel --url http://localhost:5000/mcp --bearer-token-env-var SENTINEL_MCP_TOKEN
codex mcp get sentinel
```

> **Dica no Windows:** Se `codex` não estiver no `PATH` global, você pode invocar o `codex.exe` da extensão VS Code (`%USERPROFILE%\.vscode\extensions\openai.chatgpt-*\bin\windows-x86_64\codex.exe`) ou editar o `config.toml` diretamente como na Opção A.

### Opção C — Via STDIO (`config.toml`)
Execute `dotnet build Sentinel.sln` uma vez antes. Note que `--no-launch-profile` é obrigatório para que `dotnet run` não envie texto de inicialização no `stdout` JSON-RPC:

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
  "snt_COLE_SUA_CHAVE_AQUI"
]

[mcp_servers.sentinel.env]
ASPNETCORE_ENVIRONMENT = "Development"
```

### Primeiro prompt após conectar no Codex
Reinicie ou abra uma **nova conversa** no Codex para carregar as ferramentas MCP e peça:
> *"Chame `list_workspaces` do Sentinel MCP para descobrir meu `workspaceId` e `projectId`. Depois consulte `query_context` e proponha uma decisão arquitetural usando `propose_decision` com uma URL HTTPS em `evidence`."*

---

## 4. Conectando no Claude Code

### HTTP (recomendado)
```bash
claude mcp add --transport http sentinel http://localhost:5000/mcp \
  --header "Authorization: Bearer snt_COLE_SUA_CHAVE_AQUI"
```

### STDIO local
```bash
claude mcp add sentinel -e ASPNETCORE_ENVIRONMENT=Development -- \
  dotnet run --no-build --no-launch-profile --project ./src/Sentinel.Api -- \
  --stdio --token "snt_COLE_SUA_CHAVE_AQUI"
```

---

## 5. Conectando no Cursor IDE

Crie ou edite `.cursor/mcp.json` (no projeto ou `~/.cursor/mcp.json`, nunca versione segredos):

```json
{
  "mcpServers": {
    "sentinel": {
      "url": "http://localhost:5000/mcp",
      "headers": {
        "Authorization": "Bearer snt_COLE_SUA_CHAVE_AQUI"
      }
    }
  }
}
```

---

## 6. Conectando no Gemini CLI

Em `~/.gemini/settings.json` (ou `.gemini/settings.json`):

```json
{
  "mcpServers": {
    "sentinel": {
      "httpUrl": "http://localhost:5000/mcp",
      "headers": {
        "Authorization": "Bearer snt_COLE_SUA_CHAVE_AQUI"
      }
    }
  }
}
```

---

## 7. Conectando no VS Code (GitHub Copilot / MCP Nativo)

Crie `.vscode/mcp.json`:

```json
{
  "inputs": [
    {
      "type": "promptString",
      "id": "sentinel-token",
      "description": "Chave de API snt_... gerada no Sentinel-MCP",
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

## 8. Testando a conexão passo a passo

### 8.1. Teste direto via PowerShell
```powershell
$headers = @{
  "Content-Type"  = "application/json"
  "Authorization" = "Bearer snt_COLE_SUA_CHAVE_AQUI"
}

# 1. Handshake MCP (protocolo 2025-11-25)
Invoke-RestMethod -Method Post -Uri "http://localhost:5000/mcp" -Headers $headers `
  -Body '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"codex-cli","version":"1.0"}}}' | ConvertTo-Json -Depth 10

# 2. Listar as 6 ferramentas (inputSchema, outputSchema e annotations)
Invoke-RestMethod -Method Post -Uri "http://localhost:5000/mcp" -Headers $headers `
  -Body '{"jsonrpc":"2.0","id":2,"method":"tools/list"}' | ConvertTo-Json -Depth 10

# 3. Descobrir workspaceId e projectId acessíveis à sua chave
Invoke-RestMethod -Method Post -Uri "http://localhost:5000/mcp" -Headers $headers `
  -Body '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"list_workspaces","arguments":{}}}' | ConvertTo-Json -Depth 10

# 4. Ler Resources e Prompts
Invoke-RestMethod -Method Post -Uri "http://localhost:5000/mcp" -Headers $headers `
  -Body '{"jsonrpc":"2.0","id":4,"method":"resources/read","params":{"uri":"sentinel://workspaces"}}' | ConvertTo-Json -Depth 10

Invoke-RestMethod -Method Post -Uri "http://localhost:5000/mcp" -Headers $headers `
  -Body '{"jsonrpc":"2.0","id":5,"method":"prompts/get","params":{"name":"consult_context_before_change","arguments":{"topic":"autenticacao"}}}' | ConvertTo-Json -Depth 10
```

### 8.2. Ciclo ponta a ponta no Console Web (`/review`)
1. Peça ao Codex / Claude / Cursor para submeter uma proposta com `propose_decision`.
2. Em `http://localhost:5000/review`, clique em **Atualizar** (ícone no topo) e abra **2. Governança Humana → Revisões Pendentes**.
3. Confira título, resumo, justificativa e evidências HTTPS; clique em **Aprovar Decisão** e confirme no modal.
4. Peça à IA para buscar novamente via `query_context` — ela receberá a decisão consolidada (`status: "active"`) e um `queryId`.
5. Cole o `queryId` no **Auditor de Comprovantes (SHA-256)** na aba **Conectar IAs (MCP)** para verificar a integridade criptográfica.

---

## 9. Solução de problemas comuns

| Sintoma | Causa provável | Como resolver |
| --- | --- | --- |
| `401 Unauthorized` | Chave ausente, revogada, expirada ou mal formatada. Tokens `mock:` estão desabilitados por padrão. | Gere uma chave `snt_...` válida em `/review` → **Conectar IAs (MCP)**; no Windows + VS Code Codex use `[mcp_servers.sentinel.http_headers]` em `config.toml`. |
| `NOT_FOUND` ou `FORBIDDEN` ao chamar `propose_decision` | Escopo insuficiente (ex.: chave criada como `Reader`) ou `workspaceId`/`projectId` incorreto. | Chame `list_workspaces` para copiar os UUIDs exatos; gere chave com papel `Contributor` para escrita. |
| `PROPOSAL_PENDING` (`409`) | Já existe proposta aguardando aprovação humana para o mesmo `topicKey`. | Aprove ou rejeite a proposta pendente em **Revisões Pendentes** antes de enviar outra versão. |
| `Projeto possui histórico` (`409`) ao tentar excluir projeto | Projetos com decisões, incidentes ou eventos append-only não podem ser excluídos para manter a auditoria íntegra. | Apenas projetos ainda sem eventos podem ser excluídos mediante confirmação digitada do slug. |
| Ruído JSON-RPC no `--stdio` | Faltou `--no-launch-profile` no `dotnet run`. | Use sempre `dotnet run --no-build --no-launch-profile --project ... -- --stdio`. |
