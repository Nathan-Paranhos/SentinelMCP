-- Migration 001: Initial Schema for Sentinel-MCP
-- Tables: workspaces, projects, memberships, decision_topics, incident_streams,
--         memory_events, memory_payloads, decision_current, incident_current,
--         command_receipts, context_receipts, context_receipt_items

CREATE EXTENSION IF NOT EXISTS "uuid-ossp";

-- Workspaces
CREATE TABLE IF NOT EXISTS workspaces (
    workspace_id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    name TEXT NOT NULL,
    slug TEXT NOT NULL UNIQUE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Projects
CREATE TABLE IF NOT EXISTS projects (
    project_id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    workspace_id UUID NOT NULL REFERENCES workspaces(workspace_id) ON DELETE CASCADE,
    name TEXT NOT NULL,
    slug TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT uq_projects_ws_slug UNIQUE (workspace_id, slug)
);

-- Memberships
CREATE TABLE IF NOT EXISTS memberships (
    membership_id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    workspace_id UUID NOT NULL REFERENCES workspaces(workspace_id) ON DELETE CASCADE,
    issuer TEXT NOT NULL,
    subject TEXT NOT NULL,
    role TEXT NOT NULL CHECK (role IN ('Reader', 'Contributor', 'Maintainer')),
    is_active BOOLEAN NOT NULL DEFAULT true,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT uq_memberships_ws_issuer_sub UNIQUE (workspace_id, issuer, subject)
);

-- Decision Topics
CREATE TABLE IF NOT EXISTS decision_topics (
    topic_id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    workspace_id UUID NOT NULL REFERENCES workspaces(workspace_id) ON DELETE CASCADE,
    project_id UUID NOT NULL REFERENCES projects(project_id) ON DELETE CASCADE,
    topic_key TEXT NOT NULL,
    current_version INT NOT NULL DEFAULT 0,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT uq_decision_topics_ws_proj_key UNIQUE (workspace_id, project_id, topic_key)
);

-- Incident Streams
CREATE TABLE IF NOT EXISTS incident_streams (
    incident_id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    workspace_id UUID NOT NULL REFERENCES workspaces(workspace_id) ON DELETE CASCADE,
    project_id UUID NOT NULL REFERENCES projects(project_id) ON DELETE CASCADE,
    current_version INT NOT NULL DEFAULT 1,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Memory Payloads
CREATE TABLE IF NOT EXISTS memory_payloads (
    payload_id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    workspace_id UUID NOT NULL REFERENCES workspaces(workspace_id) ON DELETE CASCADE,
    project_id UUID NOT NULL REFERENCES projects(project_id) ON DELETE CASCADE,
    title TEXT NOT NULL,
    summary TEXT NOT NULL,
    rationale TEXT,
    technical_keys JSONB NOT NULL DEFAULT '[]',
    related_incident_ids JSONB NOT NULL DEFAULT '[]',
    evidence JSONB NOT NULL DEFAULT '[]',
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Memory Events (Immutable Event Log)
CREATE TABLE IF NOT EXISTS memory_events (
    event_id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    workspace_id UUID NOT NULL REFERENCES workspaces(workspace_id) ON DELETE CASCADE,
    project_id UUID NOT NULL REFERENCES projects(project_id) ON DELETE CASCADE,
    stream_id UUID NOT NULL,
    stream_version INT NOT NULL,
    type TEXT NOT NULL,
    schema_version INT NOT NULL DEFAULT 1,
    recorded_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    author_subject TEXT NOT NULL,
    author_issuer TEXT NOT NULL,
    client_id TEXT,
    payload_id UUID REFERENCES memory_payloads(payload_id),
    ref_event_id UUID,
    CONSTRAINT uq_memory_events_stream UNIQUE (workspace_id, stream_id, stream_version)
);

CREATE INDEX IF NOT EXISTS idx_memory_events_stream_ver ON memory_events(stream_id, stream_version ASC);

-- Decision Current Projection
CREATE TABLE IF NOT EXISTS decision_current (
    topic_id UUID PRIMARY KEY REFERENCES decision_topics(topic_id) ON DELETE CASCADE,
    workspace_id UUID NOT NULL REFERENCES workspaces(workspace_id) ON DELETE CASCADE,
    project_id UUID NOT NULL REFERENCES projects(project_id) ON DELETE CASCADE,
    topic_key TEXT NOT NULL,
    active_record_id UUID,
    active_record_version INT,
    stream_version INT NOT NULL,
    status TEXT NOT NULL CHECK (status IN ('proposed', 'active', 'superseded', 'rejected')),
    pending_proposal_id UUID,
    title TEXT NOT NULL,
    summary TEXT NOT NULL,
    rationale TEXT,
    technical_keys JSONB NOT NULL DEFAULT '[]',
    related_incident_ids JSONB NOT NULL DEFAULT '[]',
    evidence JSONB NOT NULL DEFAULT '[]',
    search_vector tsvector,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_decision_current_ws_proj_status ON decision_current(workspace_id, project_id, status);
CREATE INDEX IF NOT EXISTS idx_decision_current_search ON decision_current USING GIN(search_vector);
CREATE INDEX IF NOT EXISTS idx_decision_current_tech_keys ON decision_current USING GIN(technical_keys);

-- Incident Current Projection
CREATE TABLE IF NOT EXISTS incident_current (
    incident_id UUID PRIMARY KEY REFERENCES incident_streams(incident_id) ON DELETE CASCADE,
    workspace_id UUID NOT NULL REFERENCES workspaces(workspace_id) ON DELETE CASCADE,
    project_id UUID NOT NULL REFERENCES projects(project_id) ON DELETE CASCADE,
    record_version INT NOT NULL DEFAULT 1,
    stream_version INT NOT NULL DEFAULT 1,
    status TEXT NOT NULL DEFAULT 'reported',
    title TEXT NOT NULL,
    summary TEXT NOT NULL,
    occurred_at TIMESTAMPTZ NOT NULL,
    technical_keys JSONB NOT NULL DEFAULT '[]',
    evidence JSONB NOT NULL DEFAULT '[]',
    search_vector tsvector,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_incident_current_ws_proj_status ON incident_current(workspace_id, project_id, status);
CREATE INDEX IF NOT EXISTS idx_incident_current_search ON incident_current USING GIN(search_vector);
CREATE INDEX IF NOT EXISTS idx_incident_current_tech_keys ON incident_current USING GIN(technical_keys);

-- Command Receipts (Idempotency)
CREATE TABLE IF NOT EXISTS command_receipts (
    receipt_id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    workspace_id UUID NOT NULL REFERENCES workspaces(workspace_id) ON DELETE CASCADE,
    principal_id TEXT NOT NULL,
    operation TEXT NOT NULL,
    idempotency_key TEXT NOT NULL,
    args_hash TEXT NOT NULL,
    result_json JSONB NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT uq_command_receipts_key UNIQUE (workspace_id, principal_id, operation, idempotency_key)
);

CREATE INDEX IF NOT EXISTS idx_command_receipts_created ON command_receipts(created_at);

-- Context Receipts (Query Proofs)
CREATE TABLE IF NOT EXISTS context_receipts (
    query_id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    workspace_id UUID NOT NULL REFERENCES workspaces(workspace_id) ON DELETE CASCADE,
    project_id UUID NOT NULL REFERENCES projects(project_id) ON DELETE CASCADE,
    principal_id TEXT NOT NULL,
    client_id TEXT,
    search_mode TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_context_receipts_ws_proj ON context_receipts(workspace_id, project_id);
CREATE INDEX IF NOT EXISTS idx_context_receipts_created ON context_receipts(created_at);

-- Context Receipt Items (Ordered Selected Items)
CREATE TABLE IF NOT EXISTS context_receipt_items (
    receipt_item_id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    query_id UUID NOT NULL REFERENCES context_receipts(query_id) ON DELETE CASCADE,
    workspace_id UUID NOT NULL REFERENCES workspaces(workspace_id) ON DELETE CASCADE,
    position INT NOT NULL,
    kind TEXT NOT NULL,
    stream_id UUID NOT NULL,
    record_id UUID NOT NULL,
    record_version INT NOT NULL,
    stream_version INT NOT NULL,
    status TEXT NOT NULL,
    CONSTRAINT uq_context_receipt_items_order UNIQUE (query_id, position)
);
