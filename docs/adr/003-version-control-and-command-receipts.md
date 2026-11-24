# ADR 003: Controle por Versão Esperada e Recibos de Comando Idempotentes

## Status
Aceito

## Contexto
Agentes de IA e ferramentas MCP interagem por redes sujeitas a timeouts, retentativas e requisições concorrentes. Dois agentes podem propor alterações para a mesma decisão técnica simultaneamente, ou um agente pode reenviar uma requisição de escrita após perder a conexão com o servidor sem saber se a operação foi completada com sucesso.

## Decisão
1. **Controle de Concorrência Otimista/Pessimista por Tópico (`expectedVersion`)**:
   - Cada tópico de decisão mantém um número de versão monótono incremental (`stream_version`).
   - A criação da primeira proposta exige `expectedVersion = 0`.
   - Modificações ou aprovações exigem informar a versão conhecida pelo cliente.
   - Na transação de banco, a versão corrente do tópico é bloqueada e conferida. Se divergir, a transação aborta e retorna erro estável `VERSION_CONFLICT` contendo o `topicId` e a versão atual para re-sincronização.
2. **Recibos de Comando para Idempotência (`command_receipts`)**:
   - As operações mutativas (`propose_decision`, `log_incident`, aprovações web) exigem uma chave de idempotência (`idempotencyKey`).
   - A tabela `command_receipts` possui restrição única em `(workspace_id, principal_id, operation, idempotency_key)`.
   - Se a chave for reenviada com os mesmos argumentos (conferidos via hash/fingerprint SHA-256), o servidor retorna a resposta original previamente gravada sem reinserir eventos.
   - Se a mesma chave for reenviada com argumentos diferentes, o servidor rejeita com `IDEMPOTENCY_KEY_REUSED`.

## Consequências
- Previne sobrescritas acidentais e condições de corrida entre múltiplos agentes ou revisores.
- Garante total segurança em retentativas transparentes dos clientes MCP.
- Tópicos diferentes possuem fluxos independentes e não bloqueiam uns aos outros.
- **Gatilho de revisão**: Monitorar a contenção por tópico em cenários de alta concorrência e o tamanho da tabela de recibos com expiração configurada (janela padrão de 30 dias).
