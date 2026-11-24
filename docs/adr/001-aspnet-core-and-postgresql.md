# ADR 001: ASP.NET Core e PostgreSQL para o Piloto Sentinel-MCP

## Status
Aceito

## Contexto
O Sentinel-MCP necessita de uma plataforma de alta performance, segura e robusta para disponibilizar um servidor MCP (Model Context Protocol) via Streamable HTTP, além de persistência transacional com suporte a controle de concorrência por linha, isolamento multilocatário seguro e busca em texto rico com identificadores técnicos.

Para ambientes corporativos on-premise e cloud sob demanda de governança e alta rastreabilidade, o stack técnico precisa ser unificado, com facilidade de deploy via container conteinerizado e suporte nativo a transações ACID rigorosas.

## Decisão
1. Adotar **ASP.NET Core (.NET 9 / 10)** como o runtime e framework de aplicação para a API MCP e a interface web de revisão.
2. Adotar o **SDK oficial MCP para C#** com transporte Streamable HTTP (JSON-RPC sobre HTTP/SSE).
3. Adotar **PostgreSQL (v16+)** com Npgsql como banco de dados relacional unificado, aproveitando:
   - Transações ACID estritas para escrita atômica de eventos, projeções e recibos.
   - Row Level Security (RLS) para isolamento por workspace no nível do banco de dados.
   - Busca de texto com `tsvector` e índice GIN, além de normalização de chaves técnicas.
   - Suporte futuro a `pgvector` para busca híbrida/semântica.

## Consequências
- Um único stack tecnológico em C# e PostgreSQL simplifica a infraestrutura local (Docker Compose) e em nuvem.
- Garante forte integridade referencial com chaves compostas incluindo `workspace_id`.
- Permite uso de transações de leitura determinísticas com snapshot isolation.
- **Gatilho de revisão**: Se o transporte Streamable HTTP ou a compatibilidade dos clientes de referência exigir adaptações de SSE ou WebSocket legados, reavaliar o middleware de transporte.

## Estado da implementação (2026-11)
- O runtime é **.NET 9** (`net9.0` em todos os projetos); compatível com .NET 10 via roll-forward.
- O **SDK oficial MCP para C# foi integrado**: `ModelContextProtocol.AspNetCore` atende transporte HTTP stateful em `/mcp/sse` e transporte `--stdio` via CLI para clientes locais (ex.: OpenAI Codex e Claude Code), coexistindo com o endpoint stateless `POST /mcp` com negociação para `2025-11-25`, `2025-06-18`, `2025-03-26` e `2024-11-05`.
- Todas as 6 ferramentas (`propose_decision`, `log_incident`, `query_context`, `get_timeline`, `get_context_receipt`, `list_workspaces`) publicam `inputSchema`, `outputSchema` e `annotations`, além de suporte a `resources/*` e `prompts/*`.
- Instrumentação com **OpenTelemetry** (Tracing, Métricas do ASP.NET Core, HttpClient, Runtime, contadores de ferramentas MCP e exportador OTLP configurável).
- API administrativa em `/api/admin/*` para gestão de workspaces, projetos e vínculos protegida por chave de administração (`ADMIN_API_KEY`) ou sessão web autenticada de `Maintainer` com CSRF.
- PostgreSQL com Npgsql e SQL puro, RLS, `tsvector`/GIN e chaves técnicas normalizadas estão implementados (migrações 001 a 004). `pgvector` não é usado.
- Política de segurança de conteúdo (CSP) estrita em todas as páginas HTML, sem exceções de scripts inline ou CDNs.
- O compose local usa a imagem `postgres:17-alpine`; os testes de integração usam `postgres:16-alpine` via Testcontainers.
