-- Migration 006: permite atualização administrativa controlada do nome do workspace.

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'sentinel_app') THEN
        EXECUTE 'GRANT UPDATE ON workspaces TO sentinel_app';
    END IF;
END $$;
