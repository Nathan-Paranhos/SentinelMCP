# ADR 002: Eventos de Domínio e Projeção Síncrona

## Status
Aceito

## Contexto
O Sentinel atua como fonte da verdade para decisões técnicas e incidentes de engenharia consultados por agentes de IA. Quando um mantenedor aprova uma proposta ou substitui uma decisão anterior, consultas subsequentes feitas por agentes no mesmo workspace devem refletir imediatamente o novo estado vigente (read-your-writes / strong consistency). Além disso, a auditoria e a governança exigem a capacidade de reconstruir qualquer versão passada do tópico ou reproduzir as projeções a partir do histórico limpo de eventos.

## Decisão
1. Utilizar **Event Sourcing** para o fluxo de alterações: cada proposta, aprovação, rejeição, substituição e registro de incidente é armazenado como um registro imutável em `memory_events` e seu payload correspondente em `memory_payloads`.
2. Atualizar as **projeções (`decision_current`, `incident_current`) de forma síncrona** dentro da mesma transação de banco de dados que persiste o evento e o recibo de comando.
3. Fornecer um redutor determinístico capaz de reconstruir o estado de um tópico em qualquer versão (`atVersion`), e uma rotina de reconstrução completa de projeções (`IProjectionRebuilder`).

## Consequências
- A busca e a consulta (`query_context`) observam imediatamente as decisões vigentes logo após a confirmação do commit no banco primário.
- Nenhuma dependência de filas externas ou workers assíncronos para a consistência do MVP.
- O histórico é estritamente imutável e auditável.
- **Gatilho de revisão**: Se o volume de escrita e a complexidade de indexação (como vetores densos via modelo LLM) gerarem contenção na transação de escrita, introduzir o padrão Transactional Outbox para processamento assíncrono mantendo a projeção relacional síncrona.

## Estado da implementação (2026-11)
- Implementado como descrito: evento, payload, projeção e recibo de comando na mesma transação; projeções sempre derivadas de `RedutorDeDecisao` / `RedutorDeIncidente`.
- A interface de reconstrução chama-se `IReconstrutorProjecoes` (`src/Sentinel.Core/Contratos/IRepositorios.cs`), não `IProjectionRebuilder`. É acionada pela CLI `--rebuild-projections [--workspace-id <uuid>]` e automaticamente após `--migrate` aplicar a migração 003 e após `--seed-demo` no PostgreSQL. Não há endpoint HTTP de reconstrução.
- Além de `decision_current` e `incident_current`, existe a projeção `decision_pending_proposals` (migração 003), com o conteúdo da proposta pendente exibido ao revisor.
- A migração 003 torna `memory_events` e `memory_payloads` append-only por trigger (`UPDATE`, `DELETE` e `TRUNCATE` bloqueados).
