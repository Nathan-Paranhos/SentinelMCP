# Sentinel-MCP — Guia para Agentes de IA e Engenharia

Este documento instrui agentes autônomos e desenvolvedores sobre o funcionamento, comandos e execução repetível do Sentinel-MCP.

## Pré-requisitos
- .NET 9.0 SDK ou superior (`dotnet --version`)
- PostgreSQL 16+ (local ou via Docker `compose.yaml`)
- Docker & Docker Compose (opcional para ambiente conteinerizado)

## Estrutura da Solução
- `src/Sentinel.Core`: Modelos de domínio, invariantes, eventos de domínio, redutores e casos de uso.
- `src/Sentinel.Persistence`: Acesso a dados com Npgsql/SQL puro, migrações, isolamento RLS, recibos e busca textual.
- `src/Sentinel.Api`: Servidor MCP (Streamable HTTP `/mcp`), rota web de revisão humana (`/review`) e autenticação.
- `tests/Sentinel.UnitTests`: Testes unitários de domínio, redutores, invariantes e validação de schema.
- `tests/Sentinel.ContractTests`: Testes de conformidade do protocolo MCP e schemas de entrada/saída.
- `tests/Sentinel.IntegrationTests`: Testes de integração cobrindo os critérios de aceite AC-01 a AC-08 (concorrência, isolamento, idempotência, vigência, busca e reconstrução).

## Comandos Essenciais

### Compilar a Solução
```bash
dotnet build Sentinel.sln
```

### Executar Todos os Testes
```bash
dotnet test Sentinel.sln --logger "console;verbosity=normal"
```

### Executar Testes com Filtros
```bash
# Apenas testes unitários
dotnet test tests/Sentinel.UnitTests

# Apenas critérios de aceite (integração)
dotnet test tests/Sentinel.IntegrationTests

# Apenas contratos MCP
dotnet test tests/Sentinel.ContractTests
```

### Subir Ambiente Local com Docker Compose
```bash
docker compose up -d --build
```

### Executar a API Localmente
```bash
dotnet run --project src/Sentinel.Api
```

### Popular Dados de Demonstração / Fixtures Idempotentes
```bash
dotnet run --project src/Sentinel.Api --seed-demo
```

### Reconstruir Projeções a partir de Eventos
```bash
dotnet run --project src/Sentinel.Api --rebuild-projections
```

## Smoke Test Rápido
1. Inicie a API (`dotnet run --project src/Sentinel.Api`).
2. Faça uma requisição de health check:
   `curl http://localhost:5000/healthz` -> `{"status":"Healthy"}`.
3. Inicie a sessão MCP handshake:
   `POST http://localhost:5000/mcp` com `{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"Claude Code","version":"1.0"}}}`.
4. O servidor responde com as capacidades e ferramentas disponíveis (`propose_decision`, `log_incident`, `query_context`, `get_timeline`, `get_context_receipt`).
