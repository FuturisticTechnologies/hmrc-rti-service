-- ---------------------------------------------------------------------------
-- rti schema - Milestones M6 (FPS) and M7 (EPS, error handling, reconciliation)
--
-- Row-level security lives in the migration, not in a runbook step: a control
-- that depends on someone remembering to run a script is not a control.
-- Policies use the same app.tenant_id / app.is_superuser session variables the
-- Python service sets in app/core/database.py, so both services share one
-- tenant-isolation mechanism.
-- ---------------------------------------------------------------------------

CREATE SCHEMA IF NOT EXISTS rti;
CREATE EXTENSION IF NOT EXISTS "pgcrypto";

-- --------------------------------------------------------------------------
-- Submissions
-- --------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS rti.rti_submissions (
    id                          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id                   uuid NOT NULL,
    submission_type             varchar(8)  NOT NULL,
    status                      varchar(16) NOT NULL,
    tax_year                    integer NOT NULL,
    period_number               integer NOT NULL,
    pay_run_id                  uuid NULL,
    employer_paye_reference     varchar(32) NOT NULL,
    accounts_office_reference   varchar(32) NOT NULL,
    correlation_id              varchar(64) NOT NULL,
    payload                     jsonb NOT NULL,
    payload_hash                varchar(64) NOT NULL,
    employee_count              integer NOT NULL DEFAULT 0,
    total_paye_tax              numeric(14, 2) NOT NULL DEFAULT 0,
    total_employee_ni           numeric(14, 2) NOT NULL DEFAULT 0,
    total_employer_ni           numeric(14, 2) NOT NULL DEFAULT 0,
    hmrc_submission_id          varchar(128) NULL,
    attempts                    integer NOT NULL DEFAULT 0,
    last_error                  varchar(2000) NULL,
    next_attempt_at             timestamptz NULL,
    is_live                     boolean NOT NULL DEFAULT false,
    created_by_user_id          uuid NOT NULL,
    created_at                  timestamptz NOT NULL DEFAULT now(),
    updated_at                  timestamptz NOT NULL DEFAULT now(),
    submitted_at                timestamptz NULL,

    CONSTRAINT ck_rti_submissions_type
        CHECK (submission_type IN ('Fps', 'Eps')),
    CONSTRAINT ck_rti_submissions_status
        CHECK (status IN ('Draft', 'Validated', 'Queued', 'Transmitting',
                          'Accepted', 'Rejected', 'Failed')),
    CONSTRAINT ck_rti_submissions_period
        CHECK (period_number BETWEEN 1 AND 53),
    CONSTRAINT ck_rti_submissions_tax_year
        CHECK (tax_year BETWEEN 2000 AND 2100)
);

CREATE UNIQUE INDEX IF NOT EXISTS ix_rti_submissions_correlation_id
    ON rti.rti_submissions (correlation_id);

CREATE INDEX IF NOT EXISTS ix_rti_submissions_tenant_period
    ON rti.rti_submissions (tenant_id, tax_year, period_number);

CREATE INDEX IF NOT EXISTS ix_rti_submissions_retry_queue
    ON rti.rti_submissions (next_attempt_at)
    WHERE status = 'Failed';

-- At most one accepted FPS per pay run. Partial, so superseded attempts against
-- the same pay run remain legal - an accepted FPS is amended by filing a further
-- FPS, never by resubmitting the original.
CREATE UNIQUE INDEX IF NOT EXISTS ix_rti_submissions_unique_accepted_fps
    ON rti.rti_submissions (tenant_id, pay_run_id)
    WHERE status = 'Accepted' AND submission_type = 'Fps';

-- --------------------------------------------------------------------------
-- Append-only submission audit trail
-- --------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS rti.rti_submission_events (
    id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    submission_id  uuid NOT NULL REFERENCES rti.rti_submissions (id) ON DELETE CASCADE,
    tenant_id      uuid NOT NULL,
    event_type     varchar(64) NOT NULL,
    from_status    varchar(16) NULL,
    to_status      varchar(16) NULL,
    detail         varchar(2000) NOT NULL,
    actor_user_id  uuid NULL,
    occurred_at    timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_rti_submission_events_submission
    ON rti.rti_submission_events (submission_id, occurred_at);

-- --------------------------------------------------------------------------
-- Idempotency keys - same semantics as the Python app/core/idempotency.py
-- --------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS rti.idempotency_keys (
    id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    key            varchar(128) NOT NULL,
    method         varchar(10)  NOT NULL,
    path           varchar(255) NOT NULL,
    request_hash   varchar(64)  NOT NULL,
    status_code    integer NOT NULL DEFAULT 0,
    response_body  jsonb NOT NULL DEFAULT '{}'::jsonb,
    tenant_id      uuid NULL,
    created_at     timestamptz NOT NULL DEFAULT now(),

    CONSTRAINT uq_rti_idempotency_keys UNIQUE (key, method, path)
);

-- --------------------------------------------------------------------------
-- Row-level security
--
-- FORCE matters: without it the table owner bypasses its own policies, and the
-- migration role owns these tables. The policies would look correct and be
-- silently ineffective for exactly the role we care about.
-- --------------------------------------------------------------------------
ALTER TABLE rti.rti_submissions       ENABLE ROW LEVEL SECURITY;
ALTER TABLE rti.rti_submissions       FORCE  ROW LEVEL SECURITY;
ALTER TABLE rti.rti_submission_events ENABLE ROW LEVEL SECURITY;
ALTER TABLE rti.rti_submission_events FORCE  ROW LEVEL SECURITY;

DROP POLICY IF EXISTS rti_submissions_tenant_isolation ON rti.rti_submissions;
CREATE POLICY rti_submissions_tenant_isolation ON rti.rti_submissions
    USING (
        current_setting('app.is_superuser', true) = 'true'
        OR (
            current_setting('app.tenant_id', true) <> ''
            AND tenant_id::text = current_setting('app.tenant_id', true)
        )
    )
    WITH CHECK (
        current_setting('app.is_superuser', true) = 'true'
        OR (
            current_setting('app.tenant_id', true) <> ''
            AND tenant_id::text = current_setting('app.tenant_id', true)
        )
    );

DROP POLICY IF EXISTS rti_submission_events_tenant_isolation ON rti.rti_submission_events;
CREATE POLICY rti_submission_events_tenant_isolation ON rti.rti_submission_events
    USING (
        current_setting('app.is_superuser', true) = 'true'
        OR (
            current_setting('app.tenant_id', true) <> ''
            AND tenant_id::text = current_setting('app.tenant_id', true)
        )
    )
    WITH CHECK (
        current_setting('app.is_superuser', true) = 'true'
        OR (
            current_setting('app.tenant_id', true) <> ''
            AND tenant_id::text = current_setting('app.tenant_id', true)
        )
    );

-- --------------------------------------------------------------------------
-- The event trail is evidence: insert and read only.
-- Withheld at the table level so an application bug cannot rewrite history.
-- --------------------------------------------------------------------------
REVOKE UPDATE, DELETE, TRUNCATE ON rti.rti_submission_events FROM PUBLIC;
