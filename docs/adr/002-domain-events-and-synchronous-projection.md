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
