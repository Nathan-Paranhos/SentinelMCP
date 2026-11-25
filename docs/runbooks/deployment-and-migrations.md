# Runbook: Deploy e Migrações de Banco de Dados

## Objetivo
Padronizar a preparação do PostgreSQL, a execução de migrações e a subida de uma nova versão do Sentinel-MCP.

## 1. Conceitos

### Duas connection strings
| Chave | Usuário | Uso |
| --- | --- | --- |
| `ConnectionStrings:Database` | Login de **menor privilégio**, membro de `sentinel_app`, sem superusuário e sem BYPASSRLS | Tráfego da API e `--rebuild-projections` |
| `ConnectionStrings:DatabaseAdmin` | Dono do schema / usuário com permissão de DDL | `--migrate`, `--seed-demo` (fixtures) e a reconstrução automática após a migração 003. Se ausente, o Sentinel usa `Database`. |

- Nenhuma das duas fica em `appsettings*.json`. Forneça por variável de ambiente (`ConnectionStrings__Database`, `ConnectionStrings__DatabaseAdmin`) injetada a partir do cofre de segredos.
- Com `Sentinel:StorageProvider=PostgreSQL`, `ConnectionStrings:Database` é obrigatória na inicialização, **inclusive para `--migrate`**.
- Em Production a API recusa iniciar se o login de `Database` for superusuário ou tiver BYPASSRLS (a menos que `Sentinel:Database:AllowPrivilegedRole=true`, não recomendado).

### Migrações
- Arquivos SQL em `db/migrations/` (`001_initial_schema.sql` a `006_workspace_update_grant.sql`), aplicados em ordem de nome.
- Controle em `_schema_migrations (version, applied_at)`: cada arquivo é aplicado uma única vez, numa transação própria.
- Um lock consultivo (`pg_advisory_lock`) serializa execuções concorrentes de `--migrate`.
- Se a execução aplicar a `003`, o Sentinel reconstrói as projeções em seguida (com a conexão admin). Não é preciso rodar `--rebuild-projections` depois.
- **Não há migrações de reversão.** O rollback de schema é por restauração de backup (seção 6).
- As migrações e fixtures são publicadas junto com a aplicação (pasta `db/` no diretório de saída).

## 2. Preparação única do banco (por ambiente)

### 2.1 Papel `sentinel_app`
A migração 003 cria o papel `sentinel_app` (NOLOGIN, NOSUPERUSER, NOBYPASSRLS) se a conexão admin tiver permissão para criar papéis, e concede a ele os GRANTs mínimos nas tabelas existentes.

Se a conexão admin **não** puder criar papéis, crie-o com um usuário privilegiado **antes** de aplicar a 003:
```sql
CREATE ROLE sentinel_app NOLOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
```
Se a 003 rodar sem o papel existir, os GRANTs são pulados (apenas um `NOTICE`) e a 003 fica registrada como aplicada; nesse caso, crie o papel e reaplique manualmente o bloco de GRANTs da seção 6 do arquivo `003_security_search_and_integrity.sql`.

### 2.2 Login de menor privilégio
A senha vem do cofre e nunca é gravada em arquivo, migração ou histórico do shell. Exemplo com `psql`, lendo a senha de uma variável de ambiente preenchida pelo cofre:
```bash
# SENTINEL_APP_DB_PASSWORD injetada pelo cofre no ambiente da execução
psql "$ADMIN_CONN" -v ON_ERROR_STOP=1 -v app_password="$SENTINEL_APP_DB_PASSWORD" <<'EOSQL'
CREATE ROLE sentinel_app_login LOGIN NOSUPERUSER NOBYPASSRLS PASSWORD :'app_password' IN ROLE sentinel_app;
EOSQL
```
Conferência:
```sql
SELECT rolname, rolsuper, rolbypassrls FROM pg_roles WHERE rolname IN ('sentinel_app', 'sentinel_app_login');
-- ambos devem ter rolsuper = false e rolbypassrls = false
```
No Docker Compose local, `db/docker/01-create-app-role.sh` faz 2.1 e 2.2 na primeira inicialização do volume, com a senha do `.env`.

## 3. Desenvolvimento local (Docker Compose)
```bash
cp .env.example .env            # defina POSTGRES_PASSWORD e SENTINEL_APP_DB_PASSWORD
docker compose up -d --build
```
Ordem: `postgres` (healthcheck) → `sentinel-migrate` (`--migrate` como superusuário, execução única) → `sentinel-api` (conecta como `sentinel_app_login`). Para reaplicar migrações novas depois de um `git pull`:
```bash
docker compose build
docker compose run --rm sentinel-migrate
docker compose up -d sentinel-api
```

## 4. Staging / Produção

Pré-condições: imagem da nova versão construída; conexões `Database` e `DatabaseAdmin` disponíveis no cofre; janela de mudança aprovada.

1. **Backup antes de migrar** (com a conexão admin; credenciais via `PGPASSWORD`/`.pgpass` preenchidos pelo cofre, nunca na linha de comando):
   ```bash
   pg_dump -Fc -h "$DB_HOST" -U "$DB_ADMIN_USER" -d "$DB_NAME" -f "sentinel_pre_migration_$(date +%Y%m%d%H%M).dump"
   ```
   `pg_dump` não inclui papéis (`sentinel_app`, `sentinel_app_login`); eles são objetos do cluster. Garanta que existam (ou que estejam no backup do cluster) antes de qualquer restauração.
2. **Verifique o que será aplicado**:
   ```sql
   SELECT version, applied_at FROM _schema_migrations ORDER BY version;
   ```
   Compare com os arquivos de `db/migrations/` da nova versão.
3. **Execute a migração** como tarefa única, com a imagem da nova versão:
   ```bash
   # Variáveis exigidas: Sentinel__StorageProvider=PostgreSQL (já é o padrão em Production),
   # ConnectionStrings__DatabaseAdmin e ConnectionStrings__Database.
   docker run --rm --env-file <arquivo gerado pelo cofre> <imagem-sentinel> --migrate
   ```
   Fora de container: `dotnet Sentinel.Api.dll --migrate` (binário publicado) ou `dotnet run --project src/Sentinel.Api -- --migrate`.
   O processo registra `Migrações aplicadas: ...` e, se a 003 foi aplicada, `Projeções reconstruídas após a migração 003.`, e então termina.
4. **Suba a nova versão da API** apenas com `ConnectionStrings__Database` (login de menor privilégio). Não forneça `DatabaseAdmin` ao processo da API e mantenha `Sentinel:AutoMigrate=false`.
5. **Verifique**:
   - `GET /healthz` → 200 (`Healthy`).
   - `GET /readyz` → 200 (`Ready`); 503 indica falha de acesso ao banco. A imagem não traz `curl`: use o probe do orquestrador ou um container auxiliar.
   - Logs de inicialização sem o aviso de login que ignora RLS.

## 5. Variáveis de ambiente relevantes no deploy
| Variável | Observação |
| --- | --- |
| `ASPNETCORE_ENVIRONMENT` | `Production` é o padrão da imagem. Em Production, tokens mock e login de desenvolvimento ficam desabilitados independentemente da configuração. |
| `Sentinel__StorageProvider` | `PostgreSQL` (padrão em Production via `appsettings.Production.json`). |
| `ConnectionStrings__Database` / `ConnectionStrings__DatabaseAdmin` | Do cofre. |
| `ADMIN_API_KEY` / `Sentinel__AdminApiKey` | **Obrigatória em Production** para criação inicial via `/auth/setup` e para `POST /api/admin/workspaces`. |
| `Sentinel__DataProtection__KeysDirectory` | Diretório persistente para chaves de cookie/CSRF (`compose.yaml` monta `/app/keys`). Evita perda de sessão ao reiniciar containers. |
| `Sentinel__ReverseProxy__TrustPrivateProxies` / `TrustAllProxies` | Processa `X-Forwarded-Proto`, `X-Forwarded-Host` e `X-Forwarded-For` de proxies reversos TLS (`TrustPrivateProxies=true` por padrão para loopback/VPC/Docker). |
| `Authentication__Mcp__Authority` / `Authentication__Mcp__Audience` | JWT de agentes (opcional; chaves `snt_...` SHA-256 funcionam sem OIDC). |
| `Authentication__Web__Authority` / `ClientId` / `ClientSecret` | OIDC da página de revisão (opcional; sem OIDC, `/auth/login` usa autenticação local PBKDF2-SHA256). |
| `Sentinel__ApiKeys__N__*` | Chaves de API estáticas de agentes (apenas o hash SHA-256). |

## 6. Rollback
1. Pare a nova versão da API.
2. Se a migração alterou o schema de forma incompatível com a versão anterior, restaure o backup do passo 4.1 (ver [recuperação](disaster-recovery-and-rebuild.md)). Atenção: escritas feitas entre o backup e o rollback são perdidas.
3. Suba a imagem anterior com as mesmas variáveis.
4. Repita as verificações do passo 4.5.
