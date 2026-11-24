-- Migration 005: contas locais (PBKDF2), chaves de API SHA-256 para agentes e preservação de trilha em projetos.

-- Garante que exclusão de projeto nunca apague memory_events em cascata.
ALTER TABLE memory_events DROP CONSTRAINT IF EXISTS memory_events_project_id_fkey;
ALTER TABLE memory_events ADD CONSTRAINT memory_events_project_id_fkey
    FOREIGN KEY (project_id) REFERENCES projects(project_id) ON DELETE RESTRICT;

-- Usuários locais (quando OIDC web não está configurado).
CREATE TABLE IF NOT EXISTS local_users (
    user_id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    username TEXT NOT NULL UNIQUE,
    issuer TEXT NOT NULL,
    subject TEXT NOT NULL,
    display_name TEXT,
    password_hash TEXT NOT NULL,
    is_active BOOLEAN NOT NULL DEFAULT true,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Chaves de API gerenciadas por workspace (somente hash SHA-256 é armazenado).
CREATE TABLE IF NOT EXISTS agent_api_keys (
    key_id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    workspace_id UUID NOT NULL REFERENCES workspaces(workspace_id) ON DELETE CASCADE,
    name TEXT NOT NULL,
    key_prefix TEXT NOT NULL,
    key_hash TEXT NOT NULL UNIQUE,
    issuer TEXT NOT NULL,
    subject TEXT NOT NULL,
    client_id TEXT,
    scopes JSONB NOT NULL DEFAULT '[]',
    is_active BOOLEAN NOT NULL DEFAULT true,
    created_by_subject TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    expires_at TIMESTAMPTZ,
    last_used_at TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS idx_agent_api_keys_ws ON agent_api_keys(workspace_id, created_at DESC);

ALTER TABLE agent_api_keys ENABLE ROW LEVEL SECURITY;
ALTER TABLE agent_api_keys FORCE ROW LEVEL SECURITY;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'tenant_isolation_agent_api_keys') THEN
        CREATE POLICY tenant_isolation_agent_api_keys ON agent_api_keys
            FOR ALL USING (workspace_id = NULLIF(current_setting('app.current_workspace_id', true), '')::uuid);
    END IF;

    -- Permite autenticar uma chave pelo seu hash SHA-256 sem precisar saber o workspace antes.
    IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'key_lookup_agent_api_keys') THEN
        CREATE POLICY key_lookup_agent_api_keys ON agent_api_keys
            FOR SELECT USING (key_hash = NULLIF(current_setting('app.current_key_hash', true), ''));
    END IF;

    -- Permite atualizar last_used_at apenas para a chave recém-autenticada.
    IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'key_touch_agent_api_keys') THEN
        CREATE POLICY key_touch_agent_api_keys ON agent_api_keys
            FOR UPDATE USING (key_hash = NULLIF(current_setting('app.current_key_hash', true), ''));
    END IF;
END $$;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'sentinel_app') THEN
        EXECUTE 'GRANT SELECT, INSERT, UPDATE ON local_users TO sentinel_app';
        EXECUTE 'GRANT SELECT, INSERT, UPDATE, DELETE ON agent_api_keys TO sentinel_app';
        EXECUTE 'GRANT DELETE ON projects TO sentinel_app';
    END IF;
END $$;
