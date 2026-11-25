# Índice de Documentação — Sentinel-MCP

Central de documentação para quem usa o **Sentinel-MCP** localmente, para times que implantam em infraestrutura própria da organização e para contribuidores.

---

## Navegação Rápida por Perfil

### 1. Quero rodar agora no meu computador (Uso Individual / Time Local)
1. **[README Principal](../README.md#execução-local)** — Sobe em segundos com `dotnet run` (persistência local em JSON, zero banco externo e zero mocks).
2. **[Guia de Conexão MCP](guides/conectando-agentes-mcp.md)** — Passo a passo pronto para **OpenAI Codex**, **Claude Code**, **Cursor IDE**, **Gemini CLI** e **VS Code**.

### 2. Quero hospedar na minha Organização (Produção Self-Hosted)
1. **[Runbook de Deploy e Migrações](runbooks/deployment-and-migrations.md)** — Docker Compose, PostgreSQL 16+/17 com papel de menor privilégio `sentinel_app`, variáveis de ambiente, proxy reverso TLS e DataProtection.
2. **[Runbook de Recuperação e Reconstrução](runbooks/disaster-recovery-and-rebuild.md)** — Restauração de `pg_dump`, reconstrução de projeções (`--rebuild-projections`) e queries SQL de verificação.
3. **[Política de Segurança](../SECURITY.md)** — Controles de acesso, RLS forçada, separação MCP/Web e reporte de vulnerabilidades.

### 3. Quero entender Arquitetura, Design System e Contribuir
1. **[Arquitetura, Módulos e Fluxogramas](guides/arquitetura-e-fluxos.md)** — Visão em camadas e fluxogramas Mermaid (autenticação, concorrência, idempotência, ciclo de vida de decisões e PostgreSQL RLS).
2. **[Design System ("Botanical Obsidian")](design/DESIGN_SYSTEM.md)** — Paleta, tokens semânticos Light/Dark, layout responsivo, catálogo de componentes e segurança visual.
3. **[Documento de Design de Software (SDD)](design/SDD.md)** — Especificação original do piloto + seção de estado atual da implementação.
4. **[ADRs (Architecture Decision Records)](adr/)** — Decisões arquiteturais registradas:
   - [ADR 001 — ASP.NET Core e PostgreSQL](adr/001-aspnet-core-and-postgresql.md)
   - [ADR 002 — Eventos de Domínio e Projeção Síncrona](adr/002-domain-events-and-synchronous-projection.md)
   - [ADR 003 — Controle por Versão Esperada e Idempotência](adr/003-version-control-and-command-receipts.md)
   - [ADR 004 — Isolamento por Workspace e Sessão Web](adr/004-isolation-and-web-session-review.md)
   - [ADR 005 — Busca Lexical Determinística antes da Vetorial](adr/005-lexical-search-before-vector.md)
   - [ADR 006 — Comprovante de Consulta Versionado (SHA-256)](adr/006-receipt-with-versioned-references.md)
5. **[Guia de Contribuição](../CONTRIBUTING.md)** & **[Guia para Agentes e Engenharia](../AGENTS.md)**.
