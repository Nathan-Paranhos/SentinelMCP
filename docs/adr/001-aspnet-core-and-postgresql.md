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
