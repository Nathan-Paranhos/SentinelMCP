<p align="center">
  <img src="src/Sentinel.Api/wwwroot/sentinel-verde.svg" width="104" height="104" alt="Sentinel Logo" />
</p>

<h1 align="center">Sentinel-MCP</h1>

<p align="center">
  <strong>A Memória da sua Engenharia</strong><br />
  Governança arquitetural contínua, auditoria imutável via Event Sourcing e protocolo MCP nativo para agentes de inteligência artificial e equipes de engenharia.
</p>

<p align="center">
  <img src="https://img.shields.io/badge/.NET-9.0_LTS-512BD4?style=flat-square&logo=dotnet&logoColor=white" alt=".NET 9" />
  <img src="https://img.shields.io/badge/PostgreSQL-16%2B-336791?style=flat-square&logo=postgresql&logoColor=white" alt="PostgreSQL" />
  <img src="https://img.shields.io/badge/Protocol-MCP_2024--11--05-163832?style=flat-square" alt="MCP Protocol" />
  <img src="https://img.shields.io/badge/Tests-30%2F30_Passing_(100%25)-235347?style=flat-square" alt="Test Suite" />
  <img src="https://img.shields.io/badge/Interface-Organic_macOS-8EB69B?style=flat-square" alt="macOS UX" />
  <img src="https://img.shields.io/badge/License-MIT-yellow.svg?style=flat-square" alt="License MIT" />
</p>

---

## Sumário

- [Visão Geral](#visão-geral)
  - [O Problema dos Agentes Autônomos](#o-problema-dos-agentes-autônomos)
  - [A Solução Sentinel](#a-solução-sentinel)
- [Pilares de Engenharia](#pilares-de-engenharia)
- [Arquitetura do Sistema](#arquitetura-do-sistema)
- [Interface Nativa macOS](#interface-nativa-macos)
- [Tools MCP Oficiais](#tools-mcp-oficiais)
- [Integração com Clientes MCP](#integração-com-clientes-mcp)
  - [Claude Code](#claude-code)
  - [Cursor IDE](#cursor-ide)
- [Quick Start](#quick-start)
  - [Docker Compose](#docker-compose)
  - [Desenvolvimento Local](#desenvolvimento-local)
- [Critérios de Aceite (AC-01 a AC-08)](#critérios-de-aceite-ac-01-a-ac-08)
- [Estrutura da Solução](#estrutura-da-solução)
- [Segurança & Privacidade](#segurança--privacidade)
- [Licença](#licença)

---

## 🧭 Visão Geral

O **Sentinel-MCP** é um servidor MCP (*Model Context Protocol*) de alta performance em C# sobre .NET 9 LTS que conecta agentes de desenvolvimento (como **Claude Code**, **Cursor**, **Copilot** e agentes autônomos) à memória arquitetural viva e imutável da organização.

### O Problema dos Agentes Autônomos

À medida que equipes de tecnologia escalam a utilização de agentes de IA em fluxos de codificação, análise de incidentes e revisão de PRs, surgem padrões recorrentes de falha de governança:

1. **Divergência e Propostas Recicladas**: Um agente abre um Pull Request propondo uma abordagem arquitetural que a equipe sênior já avaliou e descartou explicitamente no trimestre anterior.
2. **Desconexão com o Histórico de Incidentes**: Um agente analisa uma falha crítica em produção sem ter visibilidade do *Root Cause Analysis* (RCA) e da mitigação que já havia sido padronizada para aquele mesmo padrão de defeito.
3. **Fragmentação do Conhecimento**: Decisões técnicas ficam perdidas em descrições de PRs mesclados, mensagens de chat ou ADRs estáticas em repositórios isolados, sem validação de vigência temporal.

### A Solução Sentinel

O Sentinel estabelece um **ciclo de governança formal e auditável** entre agentes e mantenedores humanos:

```
┌─────────────────┐       propose_decision       ┌─────────────────────┐
│  Agente de IA   │ ───────────────────────────> │  Sentinel MCP Core  │
│  (Claude Code)  │                              │  (Proposta Gravada) │
└─────────────────┘                              └──────────┬──────────┘
                                                            │
                                                     Notificação / Fila
                                                            │
                                                            ▼
┌─────────────────┐    Aprovação / Rejeição      ┌─────────────────────┐
│   Mantenedor    │ ───────────────────────────> │  Interface Web      │
│     Humano      │       (Sessão Segura)        │  (Organic macOS)    │
└─────────────────┘                              └──────────┬──────────┘
                                                            │
                                                Ativação Atômica Vigente
                                                            │
                                                            ▼
┌─────────────────┐        query_context         ┌─────────────────────┐
│  Outro Agente   │ <─────────────────────────── │ Decisão Vigente     │
│    (Cursor)     │   (Comprovante Criptográfico)│ + Histórico e RCA   │
└─────────────────┘                              └─────────────────────┘
```

---

## 🏛️ Pilares de Engenharia

O Sentinel foi construído desde a base para oferecer garantias formais de consistência, concorrência e conformidade:

| Pilar | Mecanismo de Implementação |
| --- | --- |
| **Event Sourcing & Imutabilidade** | Cada proposta, aprovação, rejeição ou substituição é persistida como um evento permanente em transação ACID no PostgreSQL. O estado vigente e a busca são reconstruíveis deterministicamente a qualquer momento. |
| **Controle de Concorrência Otimista** | Escritas no mesmo tópico são serializadas com validação de `expectedVersion`. Se dois agentes tentarem atualizar o mesmo tópico simultaneamente, o servidor rejeita o segundo com `VERSION_CONFLICT`. |
| **Idempotência Estrita** | Toda operação de escrita exige `idempotencyKey`. Em caso de retentativa ou timeout de rede, o Sentinel reconhece a chave na janela de retenção e devolve o resultado original de forma idempotente, sem duplicar eventos. |
| **Isolamento Multitenant com RLS** | Segurança estrutural via *Row-Level Security* (RLS) nativo do PostgreSQL. Agentes só conseguem consultar ou propor no escopo do `workspace_id` para o qual possuem credencial autenticada. |
| **Separação Estrita de Acesso** | Nenhuma decisão é promovida a vigente de forma autônoma. Tokens Bearer MCP de agentes não possuem autorização para aprovação; a validação humana ocorre exclusivamente na interface web protegida por sessão e CSRF. |
| **Comprovantes Auditáveis de Consulta** | A ferramenta `query_context` gera comprovantes criptográficos (`context_receipts`) que gravam com precisão as versões e IDs das decisões apresentadas ao agente, sem expor ou armazenar prompts e dados confidenciais. |

---

## 📐 Arquitetura do Sistema

O sistema adota os preceitos de **Clean Architecture** e **Domain-Driven Design (DDD)**, com divisão estrita de responsabilidades:

```mermaid
flowchart TB
    subgraph Clientes["Clientes & Consumidores"]
        A1["Claude Code CLI"]
        A2["Cursor IDE"]
        A3["Agentes Autônomos"]
        H1["Mantenedor Humano"]
    end

    subgraph ApiLayer["Sentinel.Api (ASP.NET Core / .NET 9)"]
        direction TB
        McpEp["/mcp (Streamable HTTP / JSON-RPC 2.0)"]
        WebRev["/review (macOS App Shell & Auditoria)"]
        Landing["/ (Landing Page Editorial)"]
        AuthMid["Identidade & Validação de Escopos (OAuth PKCE)"]
    end

    subgraph CoreLayer["Sentinel.Core (Domínio & Regras)"]
        Commands["Comandos CQRS"]
        Reducers["Redutores Determinísticos"]
        Invariants["Validadores de Invariantes & Slugs"]
        Models["Modelos de Domínio & Eventos"]
    end

    subgraph PersistenceLayer["Sentinel.Persistence (PostgreSQL 16+ / Npgsql)"]
        Trans["Transações ACID & RLS"]
        Events[("memory_events (Append-only)")]
        Proj[("decision_current / incident_current")]
        Receipts[("command_receipts / context_receipts")]
        FTS["Busca Textual GIN (tsvector)"]
    end

    A1 -->|Streamable HTTP /mcp| McpEp
    A2 -->|Streamable HTTP /mcp| McpEp
    A3 -->|Streamable HTTP /mcp| McpEp
    H1 -->|Sessão Web Segura + CSRF| WebRev
    H1 -->|Navegação Editorial| Landing

    McpEp --> AuthMid
    WebRev --> AuthMid
    AuthMid --> Commands
    Commands --> Invariants
    Commands --> Reducers
    Reducers --> Trans
    Trans --> Events
    Trans --> Proj
    Trans --> Receipts
    Trans --> FTS
```

---

## 🖥️ Interface Nativa macOS

A interface de revisão e governança humana (`/review`) foi desenhada sob os princípios do **Apple Human Interface Guidelines** e do **macOS Sequoia**, proporcionando uma experiência de aplicativo desktop de alta precisão dentro do navegador:

- **Geometria Concêntrica**: Raios proporcionais e hierárquicos (8px, 12px, 16px, 20px, 24px, 28px).
- **Superfícies Contínuas & Acabamento Fosco**: Zero brilhos artificiais, neon ou cards genéricos.
- **Sidebar Flutuante Colapsável**: Navegação semântica entre Revisões, Decisões Vigentes, Incidentes, Linha do Tempo e Ferramentas MCP, com contador em tempo real de pendências e indicação de Workspace RLS.
- **Visualização em Split-View (Apple Notes / Finder)**: Lista compacta e refinada de registros à esquerda com visualização completa de especificações, justificativas técnicas e links de evidência à direita.
- **Spotlight Command Palette (`⌘K` ou `Ctrl+K`)**: Caixa de busca central inspirada no Raycast/Spotlight para navegação instantânea por teclado, com execução direta de comandos e filtros contextuais.
- **Painel Inspetor Lateral**: Inspeção de detalhes de auditoria, integridade SHA-256 e proveniência do agente responsável.
- **Dark Mode & Light Mode**: Paleta botânica equilibrada preservando as cores de identidade do Sentinel:
  - Preto esverdeado profundo (`#051F20`), Verde floresta (`#0B2B26`), Verde estrutural (`#163832`), Verde de interação (`#235347`), Verde sálvia (`#8EB69B`) e Verde menta suave (`#DAF1DE`).

---

## 🛠️ Tools MCP Oficiais

O Sentinel implementa a especificação oficial do protocolo MCP com suporte a chamadas estruturadas de ferramentas:

| Ferramenta | Descrição | Permissão Exigida |
| --- | --- | --- |
| `propose_decision` | Submete uma nova proposta arquitetural vinculada a um tópico técnico com justificativa e evidências HTTPS. | `write:decision` (Contributor/Maintainer) |
| `log_incident` | Registra um incidente técnico com relatório circunstanciado, chaves técnicas e evidências de RCA/mitigação. | `write:incident` (Contributor/Maintainer) |
| `query_context` | Realiza busca textual ponderada em decisões ativas e incidentes, emitindo um comprovante de consulta auditável. | `read:memory` (Reader+) |
| `get_timeline` | Reconstitui a linha do tempo e estados passados de um tópico a partir da cadeia imutável de eventos. | `read:memory` (Reader+) |
| `get_context_receipt` | Recupera a lista exata e ordenada de referências e versões disponibilizadas em uma consulta anterior. | `read:memory` (Membro do Workspace) |

### Exemplo: Submetendo uma Proposta (`propose_decision`)

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "tools/call",
  "params": {
    "name": "propose_decision",
    "arguments": {
      "workspaceId": "11111111-1111-1111-1111-111111111111",
      "projectId": "22222222-2222-2222-2222-222222222222",
      "topicKey": "payment-idempotency",
      "expectedVersion": 1,
      "title": "Chave de Idempotência Obrigatória na Cobrança Pix",
      "summary": "Toda requisição de criação de cobrança Pix deve obrigatoriamente incluir cabeçalho X-Idempotency-Key com retenção mínima de 24 horas.",
      "rationale": "Elimina concorrência e cobranças duplicadas em retentativas incertas de gateway.",
      "technicalKeys": ["pix", "idempotency", "billing"],
      "evidence": [
        {
          "type": "adr",
          "label": "ADR 003 — Idempotência de Pagamentos",
          "url": "https://example.com/adr/003-payment-idempotency"
        }
      ],
      "idempotencyKey": "prop-pix-idem-001"
    }
  }
}
```

### Exemplo: Consultando Contexto com Comprovante (`query_context`)

```json
{
  "jsonrpc": "2.0",
  "id": 2,
  "method": "tools/call",
  "params": {
    "name": "query_context",
    "arguments": {
      "workspaceId": "11111111-1111-1111-1111-111111111111",
      "projectId": "22222222-2222-2222-2222-222222222222",
      "query": "idempotency pix",
      "kind": "decision",
      "limit": 5
    }
  }
}
```

O servidor retorna o resultado correspondente acompanhado de um `queryId` persistido na tabela `context_receipts`, garantindo que auditorias possam inspecionar exatamente qual versão da decisão o modelo consumiu.

---

## 🔌 Integração com Clientes MCP

### Claude Code

Para conectar o Sentinel ao **Claude Code CLI**, configure seu arquivo de configuração MCP (`~/.claude.json` ou `claude_desktop_config.json`):

```json
{
  "mcpServers": {
    "sentinel": {
      "url": "http://localhost:5000/mcp",
      "headers": {
        "Authorization": "Bearer sentinel-dev-agent-key"
      }
    }
  }
}
```

Ou via linha de comando:

```bash
claude mcp add sentinel -- http://localhost:5000/mcp
```

### Cursor IDE

No **Cursor**, adicione nas configurações de MCP (`.cursor/mcp.json`):

```json
{
  "mcpServers": {
    "sentinel": {
      "url": "http://localhost:5000/mcp",
      "transport": "http"
    }
  }
}
```

---

## 🚀 Quick Start

### Docker Compose

A forma mais rápida de subir o ambiente completo com PostgreSQL 16 e Sentinel API:

```bash
# 1. Clone o repositório
git clone https://github.com/Nathan-Paranhos/SentinelMCP.git
cd SentinelMCP

# 2. Inicialize os containers
docker compose up -d --build

# 3. Verifique a saúde do serviço
curl http://localhost:5000/healthz
```

Acesse no seu navegador:
- **Painel de Governança macOS**: `http://localhost:5000/review`
- **Landing Page Editorial**: `http://localhost:5000/`

### Desenvolvimento Local

Pré-requisitos:
- **.NET 9.0 SDK** ou superior (`dotnet --version`)
- **PostgreSQL 16+** (opcional, o sistema inclui persistência em memória para desenvolvimento e testes)

```bash
# Compilar a solução completa
dotnet build Sentinel.sln

# Executar a API com dados de demonstração semeados
dotnet run --project src/Sentinel.Api --urls "http://localhost:5000" --seed-demo
```

### Executando os Testes Automatizados

A suíte de testes valida unitariamente e integradamente todos os critérios da solução:

```bash
# Executar todos os 30 testes da solução
dotnet test Sentinel.sln --logger "console;verbosity=normal"

# Executar apenas testes de integração e critérios de aceite
dotnet test tests/Sentinel.IntegrationTests

# Executar testes de contrato do protocolo MCP
dotnet test tests/Sentinel.ContractTests

# Executar testes unitários de domínio e invariantes
dotnet test tests/Sentinel.UnitTests
```

---

## ✅ Critérios de Aceite (AC-01 a AC-08)

Todos os 8 critérios de aceite rigorosos da arquitetura são validados e cobertos por testes automatizados em `tests/Sentinel.IntegrationTests/AcceptanceCriteriaTests.cs`:

| Critério | Descrição e Validação Formal | Situação |
| --- | --- | --- |
| **AC-01** | **Interoperabilidade**: Handshake inicial, negociação de protocolo e execução de chamadas MCP em conformidade com o padrão oficial. | **Aprovado** (100%) |
| **AC-02** | **Concorrência no Mesmo Tópico**: 10 comandos concorrentes de proposta disputando o mesmo tópico. Exatamente 1 obtém sucesso e 9 recebem conflito de versão controlado. | **Aprovado** (100%) |
| **AC-03** | **Independência entre Tópicos**: 10 tópicos técnicos distintos avançando em paralelo sem bloqueio mútuo, obtendo 100% de sucesso. | **Aprovado** (100%) |
| **AC-04** | **Idempotência & Commit Incerto**: Reenvio de comando após incerteza de rede retorna o resultado original sem duplicar eventos. Reuso de chave com payload diferente gera `IDEMPOTENCY_KEY_REUSED`. | **Aprovado** (100%) |
| **AC-05** | **Isolamento & Separação de Revisão**: Isolamento estrito entre workspaces. Rejeição de tokens Bearer MCP na rota de aprovação humana `/api/review/approve`. | **Aprovado** (100%) |
| **AC-06** | **Vigência, Substituição & Recibos**: Ciclo completo de propor, aprovar, substituir com atomicidade e reconstituir a linha do tempo preservando o comprovante original. | **Aprovado** (100%) |
| **AC-07** | **Qualidade de Busca & Latência**: Priorização determinística de identificadores técnicos exatos (`technicalKeys`) com resposta em milissegundos. | **Aprovado** (100%) |
| **AC-08** | **Recuperação & Reprodução**: Reconstrução total das projeções de leitura a partir da tabela imutável de eventos via redutor determinístico. | **Aprovado** (100%) |

---

## 📁 Estrutura da Solução

```
sentinel-mcp/
├── src/
│   ├── Sentinel.Core/              # Domínio, Invariantes, Eventos, Redutores e Comandos CQRS
│   │   ├── Comandos/               # Modelos de comando (Propor, Aprovar, Rejeitar, Restaurar)
│   │   ├── Eventos/                # Envelopes de eventos imutáveis de domínio
│   │   ├── Invariantes/            # Validadores de títulos, slugs, chaves e URLs de evidência
│   │   ├── Modelos/                # Entidades e registros de domínio
│   │   └── Redutores/              # Redutor determinístico de estado temporal
│   │
│   ├── Sentinel.Persistence/       # Persistência com PostgreSQL / SQL puro e Em Memória
│   │   ├── Contratos/              # Interfaces de repositórios desacopladas
│   │   ├── EmMemoria/              # Implementação de desenvolvimento e testes rápidos
│   │   └── PostgreSql/             # Repositório PostgreSQL com Npgsql, RLS e transações ACID
│   │
│   └── Sentinel.Api/               # Servidor MCP, Interface macOS e Handlers Web
│       ├── Auth/                   # Contexto de segurança, papéis e validação OAuth
│       ├── Mcp/                    # Dispatcher e transporte Streamable HTTP do protocolo MCP
│       ├── WebReview/              # Handlers da interface web e endpoints de revisão
│       └── wwwroot/                # Design System Organic macOS (CSS modular, JS e SVG)
│           ├── css/                # tokens.css, app-shell.css, components.css, views.css, landing.css
│           ├── js/                 # app.js, store.js, command-palette.js, icons.js
│           └── sentinel-verde.svg  # Identidade visual oficial
│
├── tests/
│   ├── Sentinel.UnitTests/         # Testes de invariantes, validadores, redutores e idempotência
│   ├── Sentinel.ContractTests/     # Testes de conformidade do protocolo MCP e schemas de tools
│   └── Sentinel.IntegrationTests/  # Testes de integração cobrindo os critérios AC-01 a AC-08
│
├── db/
│   └── migrations/                 # Migrações SQL idempotentes do banco PostgreSQL
│
├── docker-compose.yml              # Orquestração conteinerizada da API e PostgreSQL
├── Dockerfile                      # Build multi-stage para .NET 9
├── AGENTS.md                       # Diretrizes de execução repetível para desenvolvedores e agentes
└── README.md                       # Documentação principal da solução
```

---

## 🛡️ Segurança & Privacidade

- **Princípio do Menor Privilégio**: Três papéis distintos de autorização — `Reader` (apenas leitura de decisões e histórico), `Contributor` (submissão de propostas e incidentes) e `Maintainer` (aprovação, rejeição e governança estrutural).
- **Zero Retenção de Prompts**: Os comprovantes de consulta (`context_receipts`) preservam exclusivamente os identificadores técnicos e versões das decisões disponibilizadas, sem reter perguntas, resumos de conversas ou código proprietário do usuário.
- **Validação Estrita de Evidências**: URLs anexadas como evidência técnica exigem protocolo seguro HTTPS e passam por sanitização contra inclusão acidental de credenciais embutidas (`user:pass@host`) ou parâmetros sensíveis de autenticação.
- **Proteção CSRF**: A interface web de revisão exige cabeçalhos `X-CSRF-Token` validados por cookie HTTP seguro para qualquer mutação de estado.

---

## 📄 Licença

Este projeto é distribuído sob os termos da licença **MIT**. Consulte o arquivo [LICENSE](LICENSE) para obter detalhes.

---

<p align="center">
  Desenvolvido com rigor técnico para viabilizar autonomia segura em engenharia de software aumentada por inteligência artificial.
</p>
