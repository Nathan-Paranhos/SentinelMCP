# Runbook: Recuperação de Desastres e Reconstrução de Projeções

## Objetivo
Orientar a restauração de backups do PostgreSQL e a reconstrução das projeções a partir do log de eventos.

## 1. O que é fonte da verdade e o que é derivado

| Categoria | Tabelas | Recuperação |
| --- | --- | --- |
| Fonte da verdade (append-only) | `memory_events`, `memory_payloads` | Somente backup. Triggers da migração 003 impedem `UPDATE`, `DELETE` e `TRUNCATE`. |
| Cadastro | `workspaces`, `projects`, `memberships`, `decision_topics`, `incident_streams` | Somente backup (a reconstrução apenas recalcula `decision_topics.current_version`). |
| Projeções derivadas | `decision_current`, `incident_current`, `decision_pending_proposals` | `--rebuild-projections` (redutor determinístico). |
| Colunas de busca | `search_vector`, `technical_keys_norm` | Colunas geradas: recalculadas automaticamente quando a projeção é regravada. |
| Recibos | `command_receipts`, `context_receipts`, `context_receipt_items` | Somente backup. Expurgados pelo job de retenção após `Sentinel:Retention:ReceiptRetentionDays` (30 dias por padrão). |
| Papéis do cluster | `sentinel_app`, `sentinel_app_login` | Não fazem parte do `pg_dump` de um banco. Recrie (ver [deploy](deployment-and-migrations.md), seção 2) ou restaure com `pg_dumpall --roles-only`. |

## 2. Como a reconstrução funciona
- Comando: `--rebuild-projections [--workspace-id <uuid>]`. Sem `--workspace-id`, percorre todos os workspaces.
- Uma transação por workspace, com `app.current_workspace_id` definido (RLS aplicada): apaga `decision_pending_proposals`, `decision_current` e `incident_current` do workspace, recalcula `decision_topics.current_version` como a maior `stream_version` do fluxo e regrava cada projeção com o mesmo redutor usado nas escritas (`RedutorDeDecisao` / `RedutorDeIncidente`).
- Usa `ConnectionStrings:Database` (login de menor privilégio, que tem os GRANTs necessários). Exige `Sentinel:StorageProvider=PostgreSQL`.
- O `--migrate` já executa a reconstrução sozinho quando aplica a migração 003; `--seed-demo` também reconstrói após aplicar as fixtures.
- Não existe endpoint HTTP de manutenção nem modo de manutenção embutido na aplicação.

## 3. Procedimento: restaurar backup

1. **Pare a API** (ou escale para zero réplicas) para impedir escritas durante a restauração.
2. **Garanta os papéis do cluster** (`sentinel_app`, `sentinel_app_login`), pois o `pg_restore` reaplica GRANTs que dependem deles. A senha do login vem do cofre.
3. **Restaure** com a conexão admin (credenciais via `PGPASSWORD`/`.pgpass` preenchidos pelo cofre):
   ```bash
   pg_restore --clean --if-exists -h "$DB_HOST" -U "$DB_ADMIN_USER" -d "$DB_NAME" sentinel_backup.dump
   ```
   Para restaurar num banco novo, crie-o antes e omita `--clean`.
4. **Aplique migrações pendentes** se o backup for de uma versão de schema anterior à imagem que será usada (`--migrate`, ver runbook de deploy).
5. **Reconstrua as projeções** (seção 4) e **valide** (seção 5).
6. **Suba a API** e confira `/healthz` e `/readyz`.

## 4. Procedimento: reconstruir projeções

Use quando as projeções estiverem divergentes do log (por exemplo, após restauração parcial, correção de redutor ou intervenção manual em tabelas derivadas).

1. **Suspenda as escritas**: pare a API ou retire-a do balanceador. A reconstrução de cada workspace é atômica, mas não foi projetada para concorrer com escritas no mesmo workspace.
2. **Execute** com a imagem da versão em uso:
   ```bash
   # Todos os workspaces
   docker run --rm --env-file <arquivo gerado pelo cofre> <imagem-sentinel> --rebuild-projections

   # Um workspace
   docker run --rm --env-file <arquivo gerado pelo cofre> <imagem-sentinel> --rebuild-projections --workspace-id <uuid>
   ```
   Fora de container: `dotnet Sentinel.Api.dll --rebuild-projections` ou `dotnet run --project src/Sentinel.Api -- --rebuild-projections`.
   No compose local: `docker compose run --rm sentinel-migrate --rebuild-projections`.
   O processo registra `Projeções reconstruídas (...)` e termina.
3. **Valide** (seção 5).
4. **Reabra o tráfego**.

## 5. Validação pós-reconstrução

Execute com a conexão admin. Como a migração 003 aplica `FORCE ROW LEVEL SECURITY` (vale também para o dono das tabelas), defina o workspace antes de cada bloco; para um superusuário isso é inofensivo.

```sql
-- psql: \set ws '11111111-1111-1111-1111-111111111111'
SELECT set_config('app.current_workspace_id', :'ws', false);
```

**5.1 Tópicos, fluxos de decisão e projeções** (esperado: os três números iguais; o tópico é criado na mesma transação do primeiro evento):
```sql
SELECT
  (SELECT count(*) FROM decision_topics WHERE workspace_id = :'ws')                       AS topicos,
  (SELECT count(DISTINCT stream_id) FROM memory_events
     WHERE workspace_id = :'ws' AND type LIKE 'Decision%')                                AS fluxos_de_decisao,
  (SELECT count(*) FROM decision_current WHERE workspace_id = :'ws')                      AS projecoes_de_decisao;
```

**5.2 Versões coerentes com o log** (esperado: nenhuma linha):
```sql
SELECT t.topic_id, t.current_version, d.stream_version, max(e.stream_version) AS ultima_versao_no_log
FROM decision_topics t
LEFT JOIN decision_current d ON d.topic_id = t.topic_id
LEFT JOIN memory_events e ON e.workspace_id = t.workspace_id AND e.stream_id = t.topic_id
WHERE t.workspace_id = :'ws'
GROUP BY t.topic_id, t.current_version, d.stream_version
HAVING t.current_version IS DISTINCT FROM max(e.stream_version)
    OR d.stream_version IS DISTINCT FROM max(e.stream_version);
```

**5.3 Propostas pendentes** (no máximo uma linha por tópico em `decision_pending_proposals` é garantido pela chave primária `topic_id`; aqui conferimos a coerência com `decision_current`; esperado: nenhuma linha):
```sql
SELECT coalesce(p.topic_id, d.topic_id) AS topic_id, p.proposal_id, d.pending_proposal_id
FROM decision_pending_proposals p
FULL JOIN (SELECT topic_id, pending_proposal_id FROM decision_current
           WHERE workspace_id = :'ws' AND pending_proposal_id IS NOT NULL) d ON d.topic_id = p.topic_id
WHERE (p.workspace_id = :'ws' OR p.workspace_id IS NULL)
  AND p.proposal_id IS DISTINCT FROM d.pending_proposal_id;
```

**5.4 Incidentes** (esperado: números iguais):
```sql
SELECT
  (SELECT count(*) FROM incident_streams WHERE workspace_id = :'ws')                       AS fluxos_de_incidente,
  (SELECT count(DISTINCT stream_id) FROM memory_events
     WHERE workspace_id = :'ws' AND type = 'IncidentReported')                             AS fluxos_no_log,
  (SELECT count(*) FROM incident_current WHERE workspace_id = :'ws')                       AS projecoes_de_incidente;
```

**5.5 Distribuição de estados** (informativo; compare com o valor anterior ao incidente, se disponível):
```sql
SELECT status, count(*) FROM decision_current WHERE workspace_id = :'ws' GROUP BY status ORDER BY status;
```

**5.6 Proteções do schema** (uma vez por banco):
```sql
-- Log append-only: esperado 4 triggers
SELECT tgrelid::regclass AS tabela, tgname FROM pg_trigger
WHERE tgrelid IN ('memory_events'::regclass, 'memory_payloads'::regclass) AND NOT tgisinternal;

-- RLS forçada: esperado relrowsecurity = true e relforcerowsecurity = true
SELECT relname, relrowsecurity, relforcerowsecurity FROM pg_class
WHERE relname IN ('projects','memberships','decision_topics','incident_streams','memory_events','memory_payloads',
                  'decision_current','incident_current','decision_pending_proposals',
                  'command_receipts','context_receipts','context_receipt_items')
ORDER BY relname;

-- Login da API sem privilégio de ignorar RLS
SELECT rolname, rolsuper, rolbypassrls FROM pg_roles WHERE rolname IN ('sentinel_app', 'sentinel_app_login');
```

**5.7 Verificação funcional**: com uma credencial de agente válida para o ambiente, chame `query_context` num workspace conhecido e confirme que as decisões vigentes esperadas retornam; em seguida, `get_context_receipt` com o `queryId` devolvido deve trazer `integrityVerified: true`. A suíte `tests/Sentinel.IntegrationTests` (AC-08 cobre a reconstrução) roda em ambiente de teste com Testcontainers e **não** deve ser apontada para um banco de produção.

## 6. Limitações
- Não há migrações de reversão; o rollback de schema depende do backup.
- Recibos de comando expirados ou perdidos não são recuperáveis: uma retentativa com a mesma `idempotencyKey` após o expurgo é tratada como comando novo.
- Comprovantes de consulta (`context_receipts`) não são derivados e só voltam com o backup.
