-- Fixture 001: dados fictícios do piloto (idempotente).
-- Insere apenas fontes da verdade (workspaces, vínculos, fluxos, payloads e eventos).
-- As projeções (decision_current, decision_pending_proposals, incident_current) são
-- geradas pela reconstrução, que o Sentinel executa logo após aplicar esta fixture
-- (`--seed-demo`). Assim a fixture nunca diverge do redutor.

DO $$
DECLARE
    ws_id UUID := '11111111-1111-1111-1111-111111111111';
    ws_other_id UUID := '22222222-2222-2222-2222-222222222222';
    proj_id UUID := 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
    proj_other_id UUID := 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb';
    incident_stream_id UUID := 'cccccccc-cccc-cccc-cccc-cccccccccccc';
    incident_payload_id UUID := 'dddddddd-dddd-dddd-dddd-dddddddddddd';
    topic_stream_id UUID := 'c0c58a62-e095-4baf-9b56-69de5fa339ab';
    decision_payload_id UUID := 'ed377b6a-cfdb-4c94-a51a-d0a0f66a247a';
    pending_topic_id UUID := 'f0f58a62-e095-4baf-9b56-69de5fa339cc';
    pending_payload_id UUID := 'a1a77b6a-cfdb-4c94-a51a-d0a0f66a2411';
    issuer TEXT := 'https://auth.sentinel.dev';
    t_incident TIMESTAMPTZ := date_trunc('second', now()) - INTERVAL '7 days';
    t_proposal TIMESTAMPTZ := date_trunc('second', now()) - INTERVAL '5 days';
    t_approval TIMESTAMPTZ := date_trunc('second', now()) - INTERVAL '4 days';
    t_pending TIMESTAMPTZ := date_trunc('second', now()) - INTERVAL '1 day';
    proposal_event_id UUID;
BEGIN
    INSERT INTO workspaces (workspace_id, name, slug, created_at)
    VALUES
        (ws_id, 'Engenharia Central', 'eng-central', now() - INTERVAL '30 days'),
        (ws_other_id, 'Segurança Isolada', 'seg-isolada', now() - INTERVAL '29 days')
    ON CONFLICT (workspace_id) DO NOTHING;

    -- Workspace 2 ------------------------------------------------------------
    PERFORM set_config('app.current_workspace_id', ws_other_id::text, true);

    INSERT INTO projects (project_id, workspace_id, name, slug)
    VALUES (proj_other_id, ws_other_id, 'Trilhas de Auditoria', 'trilhas-auditoria')
    ON CONFLICT (project_id) DO NOTHING;

    INSERT INTO memberships (workspace_id, issuer, subject, role, is_active)
    VALUES
        (ws_other_id, issuer, 'sec-agent-1', 'Contributor', true),
        (ws_other_id, issuer, 'sec-maintainer', 'Maintainer', true)
    ON CONFLICT (workspace_id, issuer, subject) DO NOTHING;

    -- Workspace 1 ------------------------------------------------------------
    PERFORM set_config('app.current_workspace_id', ws_id::text, true);

    INSERT INTO projects (project_id, workspace_id, name, slug)
    VALUES (proj_id, ws_id, 'Pagamentos e Cobrança', 'pagamentos-cobranca')
    ON CONFLICT (project_id) DO NOTHING;

    INSERT INTO memberships (workspace_id, issuer, subject, role, is_active)
    VALUES
        (ws_id, issuer, 'agent-dev-1', 'Contributor', true),
        (ws_id, issuer, 'maintainer-alice', 'Maintainer', true),
        (ws_id, issuer, 'reader-bob', 'Reader', true)
    ON CONFLICT (workspace_id, issuer, subject) DO NOTHING;

    -- Incidente fictício: cobrança duplicada
    INSERT INTO incident_streams (incident_id, workspace_id, project_id, current_version, created_at, updated_at)
    VALUES (incident_stream_id, ws_id, proj_id, 1, t_incident, t_incident)
    ON CONFLICT (incident_id) DO NOTHING;

    INSERT INTO memory_payloads (payload_id, workspace_id, project_id, title, summary, rationale, technical_keys, related_incident_ids, evidence, created_at, occurred_at)
    VALUES (
        incident_payload_id, ws_id, proj_id,
        'Duplicação de cobrança em retentativas no gateway de pagamento',
        'Clientes sofreram cobrança duplicada após timeouts parciais no webhook do gateway externo.',
        NULL,
        '["BillingService", "WebhookController", "charge_attempts", "ERR_DUPLICATE_CHARGE"]'::jsonb,
        '[]'::jsonb,
        '[
            {"type": "incident_rca", "label": "RCA Cobrança Duplicada 2026-10", "url": "https://example.com/rca/billing-dup-202610"},
            {"type": "pull_request", "label": "PR Correção da duplicação", "url": "https://example.com/pull/42"}
        ]'::jsonb,
        t_incident, t_incident
    ) ON CONFLICT (payload_id) DO NOTHING;

    INSERT INTO memory_events (workspace_id, project_id, stream_id, stream_version, type, schema_version, recorded_at, author_subject, author_issuer, client_id, payload_id)
    VALUES (ws_id, proj_id, incident_stream_id, 1, 'IncidentReported', 1, t_incident, 'agent-dev-1', issuer, 'claude-code', incident_payload_id)
    ON CONFLICT (workspace_id, stream_id, stream_version) DO NOTHING;

    -- Decisão aprovada: idempotência de cobrança
    INSERT INTO decision_topics (topic_id, workspace_id, project_id, topic_key, current_version, created_at, updated_at)
    VALUES (topic_stream_id, ws_id, proj_id, 'payment-idempotency', 2, t_proposal, t_approval)
    ON CONFLICT (topic_id) DO NOTHING;

    INSERT INTO memory_payloads (payload_id, workspace_id, project_id, title, summary, rationale, technical_keys, related_incident_ids, evidence, created_at)
    VALUES (
        decision_payload_id, ws_id, proj_id,
        'Idempotência Obrigatória para Cobranças',
        'Usar uma chave de idempotência por tentativa lógica de cobrança com persistência transacional.',
        'Evitar cobranças duplicadas em timeouts do gateway e garantir retentativas seguras por agentes e serviços.',
        '["BillingService", "payment-idempotency", "IdempotencyKey", "charge_attempts"]'::jsonb,
        jsonb_build_array(incident_stream_id::text),
        '[
            {"type": "adr", "label": "ADR 003 Idempotência de Pagamentos", "url": "https://example.com/adr/003-payment-idempotency"},
            {"type": "pull_request", "label": "PR 42 Implementação de idempotência", "url": "https://example.com/pull/42"}
        ]'::jsonb,
        t_proposal
    ) ON CONFLICT (payload_id) DO NOTHING;

    INSERT INTO memory_events (workspace_id, project_id, stream_id, stream_version, type, schema_version, recorded_at, author_subject, author_issuer, client_id, payload_id)
    VALUES (ws_id, proj_id, topic_stream_id, 1, 'DecisionProposed', 1, t_proposal, 'agent-dev-1', issuer, 'claude-code', decision_payload_id)
    ON CONFLICT (workspace_id, stream_id, stream_version) DO NOTHING;

    SELECT event_id INTO proposal_event_id FROM memory_events
    WHERE workspace_id = ws_id AND stream_id = topic_stream_id AND stream_version = 1;

    INSERT INTO memory_events (workspace_id, project_id, stream_id, stream_version, type, schema_version, recorded_at, author_subject, author_issuer, client_id, payload_id, ref_event_id)
    VALUES (ws_id, proj_id, topic_stream_id, 2, 'DecisionApproved', 1, t_approval, 'maintainer-alice', issuer, 'web-session', decision_payload_id, proposal_event_id)
    ON CONFLICT (workspace_id, stream_id, stream_version) DO NOTHING;

    -- Proposta pendente: circuit breaker
    INSERT INTO decision_topics (topic_id, workspace_id, project_id, topic_key, current_version, created_at, updated_at)
    VALUES (pending_topic_id, ws_id, proj_id, 'payment-circuit-breaker', 1, t_pending, t_pending)
    ON CONFLICT (topic_id) DO NOTHING;

    INSERT INTO memory_payloads (payload_id, workspace_id, project_id, title, summary, rationale, technical_keys, related_incident_ids, evidence, created_at)
    VALUES (
        pending_payload_id, ws_id, proj_id,
        'Circuit Breaker Distribuído para Gateways de Pagamento',
        'Isolar falhas em cascata no gateway externo abrindo o circuito após 5 timeouts consecutivos em janela de 30 segundos.',
        'Evitar sobrecarga e exaustão de conexões HTTP durante instabilidades ou lentidão prolongada no provedor bancário externo.',
        '["BillingService", "CircuitBreaker", "payment-circuit-breaker", "Resilience"]'::jsonb,
        jsonb_build_array(incident_stream_id::text),
        '[
            {"type": "adr", "label": "ADR 004 Circuit Breaker de Cobranças", "url": "https://example.com/adr/004-circuit-breaker"},
            {"type": "pull_request", "label": "PR 58 Implementação do Circuit Breaker", "url": "https://example.com/pull/58"}
        ]'::jsonb,
        t_pending
    ) ON CONFLICT (payload_id) DO NOTHING;

    INSERT INTO memory_events (workspace_id, project_id, stream_id, stream_version, type, schema_version, recorded_at, author_subject, author_issuer, client_id, payload_id)
    VALUES (ws_id, proj_id, pending_topic_id, 1, 'DecisionProposed', 1, t_pending, 'agent-dev-1', issuer, 'claude-code', pending_payload_id)
    ON CONFLICT (workspace_id, stream_id, stream_version) DO NOTHING;
END $$;
