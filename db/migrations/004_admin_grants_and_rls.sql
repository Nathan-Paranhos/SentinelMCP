-- Migration 004: permissões administrativas mínimas para o papel sentinel_app.
-- Mantém RLS ativa e forçada em projects e memberships por workspace.

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'sentinel_app') THEN
        EXECUTE 'GRANT INSERT ON workspaces TO sentinel_app';
        EXECUTE 'GRANT INSERT ON projects TO sentinel_app';
        EXECUTE 'GRANT INSERT, UPDATE, DELETE ON memberships TO sentinel_app';
    END IF;
END $$;
