# ADR 004: Isolamento por Workspace e Separação da Sessão Web de Revisão

## Status
Aceito

## Contexto
Agentes de IA acessam ferramentas via endpoint MCP utilizando tokens Bearer emitidos via OAuth com escopos granulares (ex: `read:memory`, `write:decision`). Se um token de agente MCP pudesse aprovar ou substituir diretamente decisões de arquitetura e governança, qualquer agente vulnerável a prompt injection poderia autovalidar decisões prejudiciais ou ignorar diretrizes corporativas.

Além disso, workspaces diferentes pertencem a equipes ou unidades de negócio isoladas, exigindo garantia de que dados de um workspace nunca vazem para outro.

## Decisão
1. **Separação Estrita de Autoridade**:
   - As tools MCP só permitem `propose_decision` (status `proposed`) e `log_incident` (status `reported`), requerendo escopos `write:decision` / `write:incident` e papéis Contributor ou Maintainer.
   - **Nenhuma tool MCP permite aprovação ou rejeição de decisões**.
   - As rotas de revisão (`/review`, `/api/review/*`) exigem autenticação de sessão web humana, proteção contra CSRF e papel de **Maintainer**.
   - Tokens de agente MCP são sumariamente rejeitados nas rotas de revisão web.
2. **Isolamento por Workspace**:
   - Cada operação valida o vínculo ativo do sujeito com o workspace.
   - O PostgreSQL utiliza Row Level Security (RLS) configurado por transação via `app.current_workspace_id`.
   - Se um agente solicitar um identificador de outro workspace, a resposta é uniformemente `NOT_FOUND`, prevenindo inferência ou vazamento de existência.

## Consequências
- Proteção robusta contra ataques de Prompt Injection e autorização indevida.
- Isolamento auditável e seguro entre workspaces e projetos.
- **Gatilho de revisão**: Avaliar introdução de fluxo de aprovação por múltiplas pessoas (four-eyes principle) para ambientes com certificação SOC2/PCI-DSS.
