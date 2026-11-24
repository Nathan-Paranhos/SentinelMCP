# Runbook: Deploy e Execução de Migrações de Banco de Dados

## Objetivo
Padronizar a execução de migrações de banco de dados PostgreSQL e o ciclo de vida de deployment do Sentinel-MCP.

## 1. Estratégia de Migrações
- Migrações SQL puras e versionadas numericamente em `db/migrations/` (ex: `001_initial_schema.sql`, `002_rls_policies.sql`).
- Idempotentes ou rastreadas pela tabela interna `_schema_migrations`.
- Retrocompatibilidade: Nenhuma alteração destrutiva sem ciclo de descontinuação em 2 fases (expand/contract).

## 2. Executando em Desenvolvimento Local / Docker
```bash
# Subir PostgreSQL e executar migrações automaticamente
docker compose up -d postgres
docker compose up sentinel-migration
```

## 3. Executando em Staging / Produção
Antes de iniciar a nova versão do container `sentinel-api`:
1. Fazer snapshot do banco:
   ```bash
   pg_dump -Fc -h $DB_HOST -U $DB_USER $DB_NAME > backup_pre_migration.dump
   ```
2. Rodar o job de migração:
   ```bash
   dotnet run --project src/Sentinel.Api --migrate
   ```
3. Iniciar a aplicação Sentinel com as variáveis de ambiente validadas.
4. Executar healthcheck:
   ```bash
   curl -f http://localhost:5000/healthz
   ```
