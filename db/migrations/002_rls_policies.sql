-- Migration 002: Row Level Security (RLS) Policies
-- Enforces workspace isolation at database level using session setting 'app.current_workspace_id'

-- Enable RLS on all tenant-bound domain tables
ALTER TABLE projects ENABLE ROW LEVEL SECURITY;
ALTER TABLE memberships ENABLE ROW LEVEL SECURITY;
ALTER TABLE decision_topics ENABLE ROW LEVEL SECURITY;
ALTER TABLE incident_streams ENABLE ROW LEVEL SECURITY;
ALTER TABLE memory_events ENABLE ROW LEVEL SECURITY;
ALTER TABLE memory_payloads ENABLE ROW LEVEL SECURITY;
ALTER TABLE decision_current ENABLE ROW LEVEL SECURITY;
ALTER TABLE incident_current ENABLE ROW LEVEL SECURITY;
ALTER TABLE command_receipts ENABLE ROW LEVEL SECURITY;
ALTER TABLE context_receipts ENABLE ROW LEVEL SECURITY;
ALTER TABLE context_receipt_items ENABLE ROW LEVEL SECURITY;

-- Create Tenant Isolation Policies
DO $$
BEGIN
    -- projects
    IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'tenant_isolation_projects') THEN
        CREATE POLICY tenant_isolation_projects ON projects
            FOR ALL USING (workspace_id = NULLIF(current_setting('app.current_workspace_id', true), '')::uuid);
    END IF;

    -- memberships
    IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'tenant_isolation_memberships') THEN
        CREATE POLICY tenant_isolation_memberships ON memberships
            FOR ALL USING (workspace_id = NULLIF(current_setting('app.current_workspace_id', true), '')::uuid);
    END IF;

    -- decision_topics
    IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'tenant_isolation_decision_topics') THEN
        CREATE POLICY tenant_isolation_decision_topics ON decision_topics
            FOR ALL USING (workspace_id = NULLIF(current_setting('app.current_workspace_id', true), '')::uuid);
    END IF;

    -- incident_streams
    IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'tenant_isolation_incident_streams') THEN
        CREATE POLICY tenant_isolation_incident_streams ON incident_streams
            FOR ALL USING (workspace_id = NULLIF(current_setting('app.current_workspace_id', true), '')::uuid);
    END IF;

    -- memory_events
    IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'tenant_isolation_memory_events') THEN
        CREATE POLICY tenant_isolation_memory_events ON memory_events
            FOR ALL USING (workspace_id = NULLIF(current_setting('app.current_workspace_id', true), '')::uuid);
    END IF;

    -- memory_payloads
    IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'tenant_isolation_memory_payloads') THEN
        CREATE POLICY tenant_isolation_memory_payloads ON memory_payloads
            FOR ALL USING (workspace_id = NULLIF(current_setting('app.current_workspace_id', true), '')::uuid);
    END IF;

    -- decision_current
    IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'tenant_isolation_decision_current') THEN
        CREATE POLICY tenant_isolation_decision_current ON decision_current
            FOR ALL USING (workspace_id = NULLIF(current_setting('app.current_workspace_id', true), '')::uuid);
    END IF;

    -- incident_current
    IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'tenant_isolation_incident_current') THEN
        CREATE POLICY tenant_isolation_incident_current ON incident_current
            FOR ALL USING (workspace_id = NULLIF(current_setting('app.current_workspace_id', true), '')::uuid);
    END IF;

    -- command_receipts
    IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'tenant_isolation_command_receipts') THEN
        CREATE POLICY tenant_isolation_command_receipts ON command_receipts
            FOR ALL USING (workspace_id = NULLIF(current_setting('app.current_workspace_id', true), '')::uuid);
    END IF;

    -- context_receipts
    IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'tenant_isolation_context_receipts') THEN
        CREATE POLICY tenant_isolation_context_receipts ON context_receipts
            FOR ALL USING (workspace_id = NULLIF(current_setting('app.current_workspace_id', true), '')::uuid);
    END IF;

    -- context_receipt_items
    IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'tenant_isolation_context_receipt_items') THEN
        CREATE POLICY tenant_isolation_context_receipt_items ON context_receipt_items
            FOR ALL USING (workspace_id = NULLIF(current_setting('app.current_workspace_id', true), '')::uuid);
    END IF;
END $$;
