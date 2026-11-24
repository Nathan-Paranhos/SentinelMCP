# Runbook: Recuperação de Desastres e Reconstrução de Projeções

## Objetivo
Orientar a operação na restauração de backups e reconstrução completa das projeções (`decision_current`, `incident_current`) e índices textuais a partir do log imutável de eventos (`memory_events`).

## 1. Princípio de Reconstrução
Como o Sentinel utiliza Event Sourcing como fonte da verdade, as tabelas de projeção são derivadas e reconstruíveis de forma puramente determinística:
1. Eventos são lidos ordenados por `(stream_id, stream_version ASC)`.
2. O redutor de domínio aplica cada evento atualizando o estado do registro (ex: `proposed`, `active`, `superseded`, `rejected`).
3. As tabelas `decision_current` e `incident_current` são populadas e os vetores `tsvector` gerados novamente.

## 2. Procedimento de Reconstrução Operacional

### Passo 1: Suspender tráfego de escrita
Para garantir consistência estrita durante a reconstrução em produção ou staging:
- Ativar o modo de manutenção ou redirecionar o tráfego do endpoint `/mcp` para um nó somente-leitura.

### Passo 2: Executar comando de reconstrução
A aplicação Sentinel expõe o utilitário de reconstrução via CLI ou endpoint operacional autenticado com a chave administrativa `ADMIN_API_KEY`:

```bash
# Executando rebuild via CLI interna
dotnet run --project src/Sentinel.Api --rebuild-projections --workspace-id <optional-workspace-id>
```

Ou através do endpoint de manutenção:
```bash
curl -X POST http://localhost:5000/ops/rebuild-projections \
  -H "X-Sentinel-Admin-Key: ${SENTINEL_ADMIN_KEY}"
```

### Passo 3: Validação pós-reconstrução
Verificar:
1. Contagem total de tópicos e incidentes coincide com `SELECT count(DISTINCT stream_id) FROM memory_events`.
2. Apenas uma versão `active` por tópico em `decision_current`.
3. Executar o smoke test automatizado:
```bash
dotnet test tests/Sentinel.IntegrationTests --filter "Category=Smoke"
```

### Passo 4: Reabrir tráfego
Restabelecer o balanceador de carga para receber chamadas de escrita normalmente.
