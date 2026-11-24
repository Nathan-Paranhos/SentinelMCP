# ADR 005: Busca Lexical Determinística antes da Busca Vetorial

## Status
Aceito

## Contexto
A recuperação de contexto para agentes de IA exige alta precisão em identificadores técnicos (ex: nomes de classes, parâmetros de configuração, endpoints de API, códigos de erro) e ordenação determinística. Adicionar busca vetorial e embeddings neurais no MVP traz complexidade operacional (modelos de embeddings, dependência de GPU/APIs externas, latência de inferência e custos).

## Decisão
1. Manter uma interface estável e unificada de consulta no MCP (`query_context`), abstraindo os mecanismos internos de ranqueamento.
2. No MVP, implementar busca textual parametrizada no PostgreSQL usando `tsvector` e índice GIN, com priorização estrita de correspondências exatas em identificadores técnicos normalizados (`technical_keys`).
3. Retornar apenas decisões vigentes (`status = 'active'`) na busca padrão e incidentes reportados (`status = 'reported'`) quando solicitado.
4. Adotar desempate determinístico (relevância, data decrescente e ID estável).

## Consequências
- Baixa latência (meta p95 < 500 ms no servidor), alta confiabilidade e sem custos de inferência de embeddings no piloto.
- A ferramenta `query_context` permanece idêntica quando a busca híbrida (BM25 + vetorial via pgvector) for ativada no futuro.
- **Gatilho de revisão**: Incorporar busca híbrida com `pgvector` após a avaliação do benchmark com o corpus rotulado, desde que melhore perguntas semânticas sem degradar a precisão de identificadores exatos.
