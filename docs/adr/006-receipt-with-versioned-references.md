# ADR 006: Comprovante de Consulta com Referências Versionadas

## Status
Aceito

## Contexto
Quando um agente de IA consulta o Sentinel para tomar uma decisão técnica ou revisar código, é fundamental comprovar exatamente quais versões das decisões e evidências estavam vigentes e foram selecionadas e entregues pelo servidor naquele momento. Com o tempo, decisões sofrem substituições e novas propostas são aprovadas. Se houver um questionamento futuro de auditoria sobre a justificativa de um PR ou RCA, a equipe precisa resgatar a fotografia exata do contexto fornecido ao agente.

## Decisão
1. A cada execução de `query_context`, o servidor gera um registro imutável em `context_receipts` e seus itens em `context_receipt_items`.
2. O comprovante registra: `query_id`, `workspace_id`, `project_id`, `principal_id`, `client_id`, `search_mode`, `created_at` e a lista ordenada de registros selecionados com seus respectivos IDs, `recordVersion` e `streamVersion`.
3. **Privacidade e Auditoria Segura**: O comprovante **não armazena** a pergunta textual livre, o prompt completo do modelo ou o resumo da conversa do agente, evitando retenção indevida de dados sensíveis ou segredos corporativos.
4. O comprovante é consultável via tool `get_context_receipt`, acessível apenas pelo autor e membros autorizados do workspace.

## Consequências
- Rastreabilidade ponta a ponta: qualquer consulta histórica pode ter seu conjunto de decisões e versões reproduzido exatamente.
- Retenção padrão declarada de 30 dias para os comprovantes.
- Separação clara entre a garantia técnica (o que o servidor preparou) e a inferência do agente.
- **Gatilho de revisão**: Ajustar a janela de retenção ou políticas de expurgo automático de acordo com os requisitos de compliance da organização.
