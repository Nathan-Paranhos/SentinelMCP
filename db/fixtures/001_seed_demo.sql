-- Fixtures 001: Demo Data for Pilot
-- Seeds primary workspace, demo project, memberships, fictitious incident (duplicate billing),
-- and approved idempotency decision as described in Pilot Workflow (Section 1).

-- Fixed UUIDs for predictable testing and demos
DO $$
DECLARE
    ws_id UUID := '11111111-1111-1111-1111-111111111111';
    ws_other_id UUID := '22222222-2222-2222-2222-222222222222';
    proj_id UUID := 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
    proj_other_id UUID := 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb';
    incident_stream_id UUID := 'cccccccc-cccc-cccc-cccc-cccccccccccc';
    incident_payload_id UUID := 'dddddddd-dddd-dddd-dddd-dddddddddddd';
    topic_stream_id UUID := 'c0c58a62-e095-4baf-9b56-69de5fa339ab';
    decision_payload_id := 'ed377b6a-cfdb-4c94-a51a-d0a0f66a247a';
BEGIN
    -- 1. Workspaces
    INSERT INTO workspaces (workspace_id, name, slug)
    VALUES 
        (ws_id, 'Engineering Core', 'eng-core'),
        (ws_other_id, 'Security Isolated', 'sec-isolated')
    ON CONFLICT (workspace_id) DO NOTHING;

    -- 2. Projects
    INSERT INTO projects (project_id, workspace_id, name, slug)
    VALUES 
        (proj_id, ws_id, 'Payments & Billing', 'payments-billing'),
        (proj_other_id, ws_other_id, 'Audit Logs', 'audit-logs')
    ON CONFLICT (project_id) DO NOTHING;

    -- 3. Memberships
    INSERT INTO memberships (workspace_id, issuer, subject, role, is_active)
    VALUES
        (ws_id, 'https://auth.sentinel.dev', 'agent-dev-1', 'Contributor', true),
        (ws_id, 'https://auth.sentinel.dev', 'maintainer-alice', 'Maintainer', true),
        (ws_id, 'https://auth.sentinel.dev', 'reader-bob', 'Reader', true),
        (ws_other_id, 'https://auth.sentinel.dev', 'sec-agent-1', 'Contributor', true),
        (ws_other_id, 'https://auth.sentinel.dev', 'sec-maintainer', 'Maintainer', true)
    ON CONFLICT (workspace_id, issuer, subject) DO NOTHING;

    -- 4. Incident Stream & Payload (Fictitious Billing Duplication)
    INSERT INTO incident_streams (incident_id, workspace_id, project_id, current_version)
    VALUES (incident_stream_id, ws_id, proj_id, 1)
    ON CONFLICT (incident_id) DO NOTHING;

    INSERT INTO memory_payloads (payload_id, workspace_id, project_id, title, summary, rationale, technical_keys, related_incident_ids, evidence)
    VALUES (
        incident_payload_id,
        ws_id,
        proj_id,
        'Duplicação de cobrança em retentativas no gateway de pagamento',
        'Clientes sofreram cobrança duplicada após timeouts parciais no webhook do gateway externo.',
        'O webhook do gateway externo falhou com timeout de rede e realizou nova tentativa de entrega sem que a transação anterior tivesse sido reconciliada com chave única no banco.',
        '["BillingService", "WebhookController", "charge_attempts", "ERR_DUPLICATE_CHARGE"]'::jsonb,
        '[]'::jsonb,
        '[
            {"type": "incident_rca", "label": "RCA Cobrança Duplicada 2026-10", "url": "https://example.com/rca/billing-dup-202610"},
            {"type": "pull_request", "label": "PR Correção da duplicação", "url": "https://example.com/pull/42"}
        ]'::jsonb
    ) ON CONFLICT (payload_id) DO NOTHING;

    INSERT INTO memory_events (workspace_id, project_id, stream_id, stream_version, type, schema_version, author_subject, author_issuer, client_id, payload_id)
    VALUES (
        ws_id,
        proj_id,
        incident_stream_id,
        1,
        'IncidentReported',
        1,
        'agent-dev-1',
        'https://auth.sentinel.dev',
        'claude-code',
        incident_payload_id
    ) ON CONFLICT (workspace_id, stream_id, stream_version) DO NOTHING;

    INSERT INTO incident_current (incident_id, workspace_id, project_id, record_version, stream_version, status, title, summary, occurred_at, technical_keys, evidence, search_vector)
    VALUES (
        incident_stream_id,
        ws_id,
        proj_id,
        1,
        1,
        'reported',
        'Duplicação de cobrança em retentativas no gateway de pagamento',
        'Clientes sofreram cobrança duplicada após timeouts parciais no webhook do gateway externo.',
        now() - INTERVAL '7 days',
        '["BillingService", "WebhookController", "charge_attempts", "ERR_DUPLICATE_CHARGE"]'::jsonb,
        '[
            {"type": "incident_rca", "label": "RCA Cobrança Duplicada 2026-10", "url": "https://example.com/rca/billing-dup-202610"},
            {"type": "pull_request", "label": "PR Correção da duplicação", "url": "https://example.com/pull/42"}
        ]'::jsonb,
        to_tsvector('portuguese', 'Duplicação de cobrança em retentativas no gateway de pagamento Clientes sofreram cobrança duplicada após timeouts parciais no webhook do gateway externo')
    ) ON CONFLICT (incident_id) DO NOTHING;

    -- 5. Decision Topic & Approved ADR
    INSERT INTO decision_topics (topic_id, workspace_id, project_id, topic_key, current_version)
    VALUES (topic_stream_id, ws_id, proj_id, 'payment-idempotency', 2)
    ON CONFLICT (topic_id) DO NOTHING;

    INSERT INTO memory_payloads (payload_id, workspace_id, project_id, title, summary, rationale, technical_keys, related_incident_ids, evidence)
    VALUES (
        decision_payload_id,
        ws_id,
        proj_id,
        'Idempotência Obrigatória para Cobranças',
        'Usar uma chave de idempotência por tentativa lógica de cobrança com persistência transacional.',
        'Evitar cobranças duplicadas em timeouts do gateway e garantir retentativas seguras por agentes e serviços.',
        '["BillingService", "payment-idempotency", "IdempotencyKey", "charge_attempts"]'::jsonb,
        jsonb_build_array(incident_stream_id::text),
        '[
            {"type": "pull_request", "label": "Correção da cobrança duplicada", "url": "https://example.com/pull/42"},
            {"type": "adr", "label": "ADR 042 Idempotência de Pagamento", "url": "https://example.com/adr/042-payment-idempotency"}
        ]'::jsonb
    ) ON CONFLICT (payload_id) DO NOTHING;

    -- Event 1: Proposed
    INSERT INTO memory_events (workspace_id, project_id, stream_id, stream_version, type, schema_version, author_subject, author_issuer, client_id, payload_id)
    VALUES (
        ws_id,
        proj_id,
        topic_stream_id,
        1,
        'DecisionProposed',
        1,
        'agent-dev-1',
        'https://auth.sentinel.dev',
        'claude-code',
        decision_payload_id
    ) ON CONFLICT (workspace_id, stream_id, stream_version) DO NOTHING;

    -- Event 2: Approved
    INSERT INTO memory_events (workspace_id, project_id, stream_id, stream_version, type, schema_version, author_subject, author_issuer, client_id, payload_id)
    VALUES (
        ws_id,
        proj_id,
        topic_stream_id,
        2,
        'DecisionApproved',
        1,
        'maintainer-alice',
        'https://auth.sentinel.dev',
        'web-session',
        decision_payload_id
    ) ON CONFLICT (workspace_id, stream_id, stream_version) DO NOTHING;

    -- Current active projection
    INSERT INTO decision_current (
        topic_id, workspace_id, project_id, topic_key,
        active_record_id, active_record_version, stream_version,
        status, pending_proposal_id, title, summary, rationale,
        technical_keys, related_incident_ids, evidence, search_vector
    )
    VALUES (
        topic_stream_id, ws_id, proj_id, 'payment-idempotency',
        decision_payload_id, 2, 2,
        'active', NULL,
        'Idempotência Obrigatória para Cobranças',
        'Usar uma chave de idempotência por tentativa lógica de cobrança com persistência transacional.',
        'Evitar cobranças duplicadas em timeouts do gateway e garantir retentativas seguras por agentes e serviços.',
        '["BillingService", "payment-idempotency", "IdempotencyKey", "charge_attempts"]'::jsonb,
        jsonb_build_array(incident_stream_id::text),
        '[
            {"type": "pull_request", "label": "Correção da cobrança duplicada", "url": "https://example.com/pull/42"},
            {"type": "adr", "label": "ADR 042 Idempotência de Pagamento", "url": "https://example.com/adr/042-payment-idempotency"}
        ]'::jsonb,
        to_tsvector('portuguese', 'Idempotência Obrigatória para Cobranças Usar uma chave de idempotência por tentativa lógica de cobrança com persistência transacional Evitar cobranças duplicadas em timeouts do gateway')
    ) ON CONFLICT (topic_id) DO NOTHING;

END $$;
