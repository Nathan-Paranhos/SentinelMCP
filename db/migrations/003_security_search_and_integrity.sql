-- Migration 003: integridade, isolamento efetivo, busca gerada e log imutável.
--
-- Depois de aplicar esta migração, execute a reconstrução das projeções
-- (`--rebuild-projections`); o Sentinel faz isso automaticamente quando ele
-- próprio aplica esta migração.

-- ---------------------------------------------------------------------------
-- 1. Colunas novas
-- ---------------------------------------------------------------------------
-- Instante informado pelo autor (incidentes): fica no payload para o rebuild reproduzi-lo.
ALTER TABLE memory_payloads ADD COLUMN IF NOT EXISTS occurred_at TIMESTAMPTZ;
-- Motivo de rejeição registrado no próprio evento.
ALTER TABLE memory_events ADD COLUMN IF NOT EXISTS reason TEXT;
-- Comprovantes: emissor da identidade autora e digest SHA-256 do conteúdo.
ALTER TABLE context_receipts ADD COLUMN IF NOT EXISTS principal_issuer TEXT;
ALTER TABLE context_receipts ADD COLUMN IF NOT EXISTS digest TEXT;

CREATE INDEX IF NOT EXISTS idx_memory_events_ws_recorded ON memory_events(workspace_id, recorded_at DESC);
CREATE INDEX IF NOT EXISTS idx_command_receipts_ws_created ON command_receipts(workspace_id, created_at);
CREATE INDEX IF NOT EXISTS idx_context_receipts_ws_created ON context_receipts(workspace_id, created_at);

-- ---------------------------------------------------------------------------
-- 2. Busca: tsvector e chaves técnicas normalizadas como colunas geradas
-- ---------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION sentinel_normalize_keys(keys jsonb)
RETURNS text[] LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
    SELECT CASE WHEN jsonb_typeof(keys) = 'array'
        THEN coalesce((SELECT array_agg(lower(btrim(k))) FROM jsonb_array_elements_text(keys) AS k), '{}'::text[])
        ELSE '{}'::text[] END
$$;

CREATE OR REPLACE FUNCTION sentinel_keys_text(keys jsonb)
RETURNS text LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
    SELECT CASE WHEN jsonb_typeof(keys) = 'array'
        THEN coalesce((SELECT string_agg(k, ' ') FROM jsonb_array_elements_text(keys) AS k), '')
        ELSE '' END
$$;

DROP INDEX IF EXISTS idx_decision_current_search;
ALTER TABLE decision_current DROP COLUMN IF EXISTS search_vector;
ALTER TABLE decision_current ADD COLUMN search_vector tsvector GENERATED ALWAYS AS (
    setweight(to_tsvector('simple'::regconfig, sentinel_keys_text(technical_keys)), 'A') ||
    setweight(to_tsvector('portuguese'::regconfig, coalesce(title, '')), 'A') ||
    setweight(to_tsvector('simple'::regconfig, coalesce(title, '')), 'B') ||
    setweight(to_tsvector('portuguese'::regconfig, coalesce(summary, '')), 'B') ||
    setweight(to_tsvector('simple'::regconfig, coalesce(summary, '')), 'C') ||
    setweight(to_tsvector('portuguese'::regconfig, coalesce(rationale, '')), 'D')
) STORED;
ALTER TABLE decision_current ADD COLUMN IF NOT EXISTS technical_keys_norm text[]
    GENERATED ALWAYS AS (sentinel_normalize_keys(technical_keys)) STORED;
CREATE INDEX IF NOT EXISTS idx_decision_current_search ON decision_current USING GIN(search_vector);
CREATE INDEX IF NOT EXISTS idx_decision_current_keys_norm ON decision_current USING GIN(technical_keys_norm);

DROP INDEX IF EXISTS idx_incident_current_search;
ALTER TABLE incident_current DROP COLUMN IF EXISTS search_vector;
ALTER TABLE incident_current ADD COLUMN search_vector tsvector GENERATED ALWAYS AS (
    setweight(to_tsvector('simple'::regconfig, sentinel_keys_text(technical_keys)), 'A') ||
    setweight(to_tsvector('portuguese'::regconfig, coalesce(title, '')), 'A') ||
    setweight(to_tsvector('simple'::regconfig, coalesce(title, '')), 'B') ||
    setweight(to_tsvector('portuguese'::regconfig, coalesce(summary, '')), 'B') ||
    setweight(to_tsvector('simple'::regconfig, coalesce(summary, '')), 'C')
) STORED;
ALTER TABLE incident_current ADD COLUMN IF NOT EXISTS technical_keys_norm text[]
    GENERATED ALWAYS AS (sentinel_normalize_keys(technical_keys)) STORED;
CREATE INDEX IF NOT EXISTS idx_incident_current_search ON incident_current USING GIN(search_vector);
CREATE INDEX IF NOT EXISTS idx_incident_current_keys_norm ON incident_current USING GIN(technical_keys_norm);

-- ---------------------------------------------------------------------------
-- 3. Projeção própria para propostas pendentes (o revisor vê o conteúdo proposto)
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS decision_pending_proposals (
    topic_id UUID PRIMARY KEY REFERENCES decision_topics(topic_id) ON DELETE CASCADE,
    workspace_id UUID NOT NULL REFERENCES workspaces(workspace_id),
    project_id UUID NOT NULL REFERENCES projects(project_id),
    topic_key TEXT NOT NULL,
    stream_version INT NOT NULL,
    proposal_id UUID NOT NULL REFERENCES memory_payloads(payload_id),
    title TEXT NOT NULL,
    summary TEXT NOT NULL,
    rationale TEXT,
    technical_keys JSONB NOT NULL DEFAULT '[]',
    related_incident_ids JSONB NOT NULL DEFAULT '[]',
    evidence JSONB NOT NULL DEFAULT '[]',
    author_subject TEXT NOT NULL,
    author_issuer TEXT NOT NULL,
    client_id TEXT,
    proposed_at TIMESTAMPTZ NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_decision_pending_ws_proj ON decision_pending_proposals(workspace_id, project_id, proposed_at DESC);

-- ---------------------------------------------------------------------------
-- 4. RLS efetivo: FORCE (vale também para o dono das tabelas) e políticas
-- ---------------------------------------------------------------------------
ALTER TABLE decision_pending_proposals ENABLE ROW LEVEL SECURITY;

DO $$
DECLARE
    t TEXT;
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'tenant_isolation_decision_pending_proposals') THEN
        CREATE POLICY tenant_isolation_decision_pending_proposals ON decision_pending_proposals
            FOR ALL USING (workspace_id = NULLIF(current_setting('app.current_workspace_id', true), '')::uuid);
    END IF;

    -- Permite à identidade autenticada listar os próprios vínculos (seleção de workspace na
    -- página de revisão) sem precisar de função SECURITY DEFINER.
    IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'identity_lookup_memberships') THEN
        CREATE POLICY identity_lookup_memberships ON memberships
            FOR SELECT USING (
                issuer = NULLIF(current_setting('app.current_issuer', true), '')
                AND subject = NULLIF(current_setting('app.current_subject', true), ''));
    END IF;

    FOREACH t IN ARRAY ARRAY[
        'projects', 'memberships', 'decision_topics', 'incident_streams', 'memory_events',
        'memory_payloads', 'decision_current', 'incident_current', 'decision_pending_proposals',
        'command_receipts', 'context_receipts', 'context_receipt_items']
    LOOP
        EXECUTE format('ALTER TABLE %I FORCE ROW LEVEL SECURITY', t);
    END LOOP;
END $$;

-- ---------------------------------------------------------------------------
-- 5. Log de eventos imutável (append-only)
-- ---------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION sentinel_forbid_mutation()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'A tabela % é append-only: operação % não permitida.', TG_TABLE_NAME, TG_OP
        USING ERRCODE = 'insufficient_privilege';
END $$;

DROP TRIGGER IF EXISTS trg_memory_events_immutable ON memory_events;
CREATE TRIGGER trg_memory_events_immutable BEFORE UPDATE OR DELETE ON memory_events
    FOR EACH ROW EXECUTE FUNCTION sentinel_forbid_mutation();
DROP TRIGGER IF EXISTS trg_memory_events_no_truncate ON memory_events;
CREATE TRIGGER trg_memory_events_no_truncate BEFORE TRUNCATE ON memory_events
    FOR EACH STATEMENT EXECUTE FUNCTION sentinel_forbid_mutation();

DROP TRIGGER IF EXISTS trg_memory_payloads_immutable ON memory_payloads;
CREATE TRIGGER trg_memory_payloads_immutable BEFORE UPDATE OR DELETE ON memory_payloads
    FOR EACH ROW EXECUTE FUNCTION sentinel_forbid_mutation();
DROP TRIGGER IF EXISTS trg_memory_payloads_no_truncate ON memory_payloads;
CREATE TRIGGER trg_memory_payloads_no_truncate BEFORE TRUNCATE ON memory_payloads
    FOR EACH STATEMENT EXECUTE FUNCTION sentinel_forbid_mutation();

-- Remover um workspace não pode mais apagar o histórico em cascata.
ALTER TABLE memory_events DROP CONSTRAINT IF EXISTS memory_events_workspace_id_fkey;
ALTER TABLE memory_events ADD CONSTRAINT memory_events_workspace_id_fkey
    FOREIGN KEY (workspace_id) REFERENCES workspaces(workspace_id) ON DELETE RESTRICT;
ALTER TABLE memory_events DROP CONSTRAINT IF EXISTS memory_events_project_id_fkey;
ALTER TABLE memory_events ADD CONSTRAINT memory_events_project_id_fkey
    FOREIGN KEY (project_id) REFERENCES projects(project_id) ON DELETE RESTRICT;
ALTER TABLE memory_payloads DROP CONSTRAINT IF EXISTS memory_payloads_workspace_id_fkey;
ALTER TABLE memory_payloads ADD CONSTRAINT memory_payloads_workspace_id_fkey
    FOREIGN KEY (workspace_id) REFERENCES workspaces(workspace_id) ON DELETE RESTRICT;
ALTER TABLE memory_payloads DROP CONSTRAINT IF EXISTS memory_payloads_project_id_fkey;
ALTER TABLE memory_payloads ADD CONSTRAINT memory_payloads_project_id_fkey
    FOREIGN KEY (project_id) REFERENCES projects(project_id) ON DELETE RESTRICT;

-- ---------------------------------------------------------------------------
-- 6. Papel de aplicação com menor privilégio
-- ---------------------------------------------------------------------------
-- O usuário de login da aplicação deve ser membro de sentinel_app e NÃO pode ser
-- superusuário nem ter BYPASSRLS (o Sentinel recusa iniciar em Production nesse caso).
-- A senha do login nunca fica em migração: crie-o pelo cofre/infra, por exemplo:
--   CREATE ROLE sentinel_app_login LOGIN PASSWORD '<do cofre>' IN ROLE sentinel_app;
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'sentinel_app') THEN
        CREATE ROLE sentinel_app NOLOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
    END IF;
EXCEPTION WHEN insufficient_privilege THEN
    RAISE NOTICE 'Sem privilégio para criar o papel sentinel_app; crie-o manualmente e reaplique os GRANTs desta migração.';
END $$;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'sentinel_app') THEN
        EXECUTE 'GRANT USAGE ON SCHEMA public TO sentinel_app';
        EXECUTE 'GRANT SELECT ON workspaces, projects, memberships TO sentinel_app';
        EXECUTE 'GRANT SELECT, INSERT ON memory_events, memory_payloads TO sentinel_app';
        EXECUTE 'GRANT SELECT, INSERT, UPDATE ON decision_topics, incident_streams TO sentinel_app';
        EXECUTE 'GRANT SELECT, INSERT, UPDATE, DELETE ON decision_current, incident_current, decision_pending_proposals TO sentinel_app';
        EXECUTE 'GRANT SELECT, INSERT, DELETE ON command_receipts, context_receipts, context_receipt_items TO sentinel_app';
        EXECUTE 'GRANT EXECUTE ON FUNCTION sentinel_normalize_keys(jsonb), sentinel_keys_text(jsonb) TO sentinel_app';
    END IF;
END $$;

REVOKE UPDATE, DELETE, TRUNCATE ON memory_events, memory_payloads FROM PUBLIC;
