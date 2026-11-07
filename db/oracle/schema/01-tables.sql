-- Patient feedback schema (owned by PF_OWNER). All timestamps are UTC (TIMESTAMP without time zone, written as
-- SYS_EXTRACT_UTC(SYSTIMESTAMP) or by the application from its TimeProvider).
-- Personal data columns end in _ct: AES-256-GCM ciphertext produced by the application (envelope encryption).
-- The database never sees a plaintext name, phone number, comment or note.

CREATE TABLE contact (
    contact_id           VARCHAR2(32)  NOT NULL,
    patient_ref_bidx     RAW(32),                 -- HMAC blind index of the HIS patient reference
    phone_bidx           RAW(32),                 -- HMAC blind index of the phone number (inbound STOP replies)
    full_name_ct         RAW(1000),
    phone_ct             RAW(100),
    wrapped_dek          RAW(100),                -- per-contact data key, wrapped by the KEK named in kek_id
    kek_id               VARCHAR2(32),
    preferred_channel    VARCHAR2(10)  NOT NULL,
    whatsapp_opt_out_at  TIMESTAMP,
    sms_opt_out_at       TIMESTAMP,
    created_at           TIMESTAMP     NOT NULL,
    last_activity_at     TIMESTAMP     NOT NULL,  -- retention clock: last visit, answer or case activity
    anonymised_at        TIMESTAMP,
    CONSTRAINT contact_pk PRIMARY KEY (contact_id),
    CONSTRAINT contact_channel_ck CHECK (preferred_channel IN ('WHATSAPP', 'SMS')),
    -- An anonymised row cannot keep any identifying column: enforced by the database, not by convention.
    CONSTRAINT contact_anonymised_ck CHECK (
        anonymised_at IS NULL
        OR (patient_ref_bidx IS NULL AND phone_bidx IS NULL AND full_name_ct IS NULL AND phone_ct IS NULL AND wrapped_dek IS NULL AND kek_id IS NULL)),
    CONSTRAINT contact_live_ck CHECK (
        anonymised_at IS NOT NULL
        OR (patient_ref_bidx IS NOT NULL AND phone_bidx IS NOT NULL AND full_name_ct IS NOT NULL AND phone_ct IS NOT NULL AND wrapped_dek IS NOT NULL AND kek_id IS NOT NULL))
)
/
-- Oracle does not index all-NULL keys, so anonymised rows (NULL index) never collide.
CREATE UNIQUE INDEX contact_bidx_ux ON contact (patient_ref_bidx)
/
-- Function-based index = partial index: only live rows are indexed, which is exactly what the retention scan reads.
CREATE INDEX contact_phone_bidx_ix ON contact (phone_bidx)
/
CREATE INDEX contact_retention_ix ON contact (CASE WHEN anonymised_at IS NULL THEN last_activity_at END)
/

-- One row per accepted webhook delivery. The primary key is the Standard Webhooks message id: a replayed delivery
-- (same id, still inside the signature tolerance window) hits the key and is acknowledged without re-processing.
CREATE TABLE clinical_event (
    event_id      VARCHAR2(64)  NOT NULL,
    event_type    VARCHAR2(32)  NOT NULL,
    visit_id      VARCHAR2(64)  NOT NULL,
    contact_id    VARCHAR2(32),
    department    VARCHAR2(64)  NOT NULL,
    completed_at  TIMESTAMP     NOT NULL,
    received_at   TIMESTAMP     NOT NULL,
    outcome       VARCHAR2(20)  NOT NULL,
    CONSTRAINT clinical_event_pk PRIMARY KEY (event_id),
    CONSTRAINT clinical_event_contact_fk FOREIGN KEY (contact_id) REFERENCES contact (contact_id),
    CONSTRAINT clinical_event_outcome_ck CHECK (outcome IN ('SURVEY_SCHEDULED', 'NO_CONSENT', 'DUPLICATE_VISIT', 'OPTED_OUT'))
)
/

CREATE TABLE survey (
    survey_id      NUMBER GENERATED ALWAYS AS IDENTITY,
    event_id       VARCHAR2(64)  NOT NULL,
    visit_id       VARCHAR2(64)  NOT NULL,
    contact_id     VARCHAR2(32)  NOT NULL,
    department     VARCHAR2(64)  NOT NULL,
    token_hash     RAW(32),                       -- SHA-256 of the link token; NULL once used or expired
    status         VARCHAR2(16)  NOT NULL,
    created_at     TIMESTAMP     NOT NULL,
    scheduled_for  TIMESTAMP     NOT NULL,
    sent_at        TIMESTAMP,
    expires_at     TIMESTAMP     NOT NULL,
    completed_at   TIMESTAMP,
    CONSTRAINT survey_pk PRIMARY KEY (survey_id),
    CONSTRAINT survey_visit_uk UNIQUE (visit_id),  -- one survey per visit even if the HIS sends the event twice
    CONSTRAINT survey_token_uk UNIQUE (token_hash),
    CONSTRAINT survey_event_fk FOREIGN KEY (event_id) REFERENCES clinical_event (event_id),
    CONSTRAINT survey_contact_fk FOREIGN KEY (contact_id) REFERENCES contact (contact_id),
    CONSTRAINT survey_status_ck CHECK (status IN ('SCHEDULED', 'SENT', 'COMPLETED', 'EXPIRED', 'UNDELIVERABLE', 'OPTED_OUT'))
)
/
CREATE INDEX survey_contact_ix ON survey (contact_id)
/
CREATE INDEX survey_expiry_ix ON survey (CASE WHEN status IN ('SCHEDULED', 'SENT') THEN expires_at END)
/

-- Transactional outbox: written in the same transaction as the survey, drained by the dispatcher.
CREATE TABLE outbox_message (
    message_id           NUMBER GENERATED ALWAYS AS IDENTITY,
    survey_id            NUMBER        NOT NULL,
    channel              VARCHAR2(10)  NOT NULL,
    status               VARCHAR2(10)  NOT NULL,
    attempts             NUMBER(3)     DEFAULT 0 NOT NULL,   -- attempts on the current channel
    total_attempts       NUMBER(3)     DEFAULT 0 NOT NULL,
    token_ct             RAW(200),                           -- link token encrypted with the contact's data key until sent
    next_attempt_at      TIMESTAMP     NOT NULL,
    lease_until          TIMESTAMP,
    claim_token          RAW(16),
    last_error           VARCHAR2(200),
    provider_message_id  VARCHAR2(100),
    created_at           TIMESTAMP     NOT NULL,
    sent_at              TIMESTAMP,
    CONSTRAINT outbox_message_pk PRIMARY KEY (message_id),
    CONSTRAINT outbox_survey_fk FOREIGN KEY (survey_id) REFERENCES survey (survey_id),
    CONSTRAINT outbox_channel_ck CHECK (channel IN ('WHATSAPP', 'SMS')),
    CONSTRAINT outbox_status_ck CHECK (status IN ('PENDING', 'IN_FLIGHT', 'SENT', 'DEAD', 'CANCELLED'))
)
/
-- Only claimable rows are indexed: the hot index stays tiny however large the history grows.
CREATE INDEX outbox_due_ix ON outbox_message (CASE WHEN status IN ('PENDING', 'IN_FLIGHT') THEN next_attempt_at END)
/
CREATE INDEX outbox_claim_ix ON outbox_message (claim_token)
/
CREATE INDEX outbox_survey_ix ON outbox_message (survey_id)
/

CREATE TABLE survey_response (
    survey_id     NUMBER        NOT NULL,
    nps           NUMBER(2)     NOT NULL,
    csat          NUMBER(1),
    ces           NUMBER(1),
    comment_ct    RAW(2000),
    submitted_at  TIMESTAMP     NOT NULL,
    CONSTRAINT survey_response_pk PRIMARY KEY (survey_id),
    CONSTRAINT survey_response_survey_fk FOREIGN KEY (survey_id) REFERENCES survey (survey_id),
    CONSTRAINT survey_response_nps_ck CHECK (nps BETWEEN 0 AND 10),
    CONSTRAINT survey_response_csat_ck CHECK (csat BETWEEN 1 AND 5),
    CONSTRAINT survey_response_ces_ck CHECK (ces BETWEEN 1 AND 7)
)
/
CREATE INDEX survey_response_time_ix ON survey_response (submitted_at)
/

CREATE TABLE feedback_case (
    case_id      NUMBER GENERATED ALWAYS AS IDENTITY,
    survey_id    NUMBER         NOT NULL,
    contact_id   VARCHAR2(32)   NOT NULL,
    department   VARCHAR2(64)   NOT NULL,
    severity     NUMBER(1)      NOT NULL,
    reasons      VARCHAR2(400)  NOT NULL,
    status       VARCHAR2(12)   NOT NULL,
    opened_at    TIMESTAMP      NOT NULL,
    sla_due_at   TIMESTAMP      NOT NULL,
    assigned_to  VARCHAR2(64),
    resolved_at  TIMESTAMP,
    sla_breached CHAR(1)        DEFAULT 'N' NOT NULL,
    CONSTRAINT feedback_case_pk PRIMARY KEY (case_id),
    CONSTRAINT feedback_case_survey_uk UNIQUE (survey_id),
    CONSTRAINT feedback_case_survey_fk FOREIGN KEY (survey_id) REFERENCES survey (survey_id),
    CONSTRAINT feedback_case_contact_fk FOREIGN KEY (contact_id) REFERENCES contact (contact_id),
    CONSTRAINT feedback_case_severity_ck CHECK (severity BETWEEN 1 AND 3),
    CONSTRAINT feedback_case_status_ck CHECK (status IN ('OPEN', 'IN_PROGRESS', 'RESOLVED')),
    CONSTRAINT feedback_case_breach_ck CHECK (sla_breached IN ('Y', 'N'))
)
/
-- The panel and the SLA watcher read unresolved cases ordered by due time.
CREATE INDEX feedback_case_open_ix ON feedback_case (CASE WHEN status <> 'RESOLVED' THEN sla_due_at END)
/
CREATE INDEX feedback_case_contact_ix ON feedback_case (contact_id, status)
/
CREATE INDEX feedback_case_opened_ix ON feedback_case (opened_at)
/

CREATE TABLE case_event (
    case_event_id  NUMBER GENERATED ALWAYS AS IDENTITY,
    case_id        NUMBER        NOT NULL,
    at             TIMESTAMP     NOT NULL,
    actor          VARCHAR2(64)  NOT NULL,
    action         VARCHAR2(16)  NOT NULL,
    note_ct        RAW(2000),
    CONSTRAINT case_event_pk PRIMARY KEY (case_event_id),
    CONSTRAINT case_event_case_fk FOREIGN KEY (case_id) REFERENCES feedback_case (case_id),
    CONSTRAINT case_event_action_ck CHECK (action IN ('OPENED', 'ASSIGNED', 'NOTE', 'RESOLVED', 'SLA_BREACHED', 'REOPENED'))
)
/
CREATE INDEX case_event_case_ix ON case_event (case_id, at)
/

-- Append-only: the application role has INSERT and SELECT only (03-grants.sql). Only the retention package, running
-- with the owner's rights, deletes rows older than the audit retention period.
CREATE TABLE audit_log (
    audit_id   NUMBER GENERATED ALWAYS AS IDENTITY,
    at         TIMESTAMP      DEFAULT SYS_EXTRACT_UTC(SYSTIMESTAMP) NOT NULL,
    actor      VARCHAR2(64)   NOT NULL,
    action     VARCHAR2(16)   NOT NULL,
    entity     VARCHAR2(24)   NOT NULL,
    entity_id  VARCHAR2(64)   NOT NULL,
    fields     VARCHAR2(200),
    purpose    VARCHAR2(64)   NOT NULL,
    trace_id   VARCHAR2(32),
    db_user    VARCHAR2(128)  DEFAULT SYS_CONTEXT('USERENV', 'SESSION_USER') NOT NULL,
    CONSTRAINT audit_log_pk PRIMARY KEY (audit_id),
    CONSTRAINT audit_log_action_ck CHECK (action IN ('PII_WRITE', 'PII_READ', 'PII_REVEAL', 'PII_ANONYMISE', 'PII_ERASE', 'OPT_OUT'))
)
/
CREATE INDEX audit_log_entity_ix ON audit_log (entity, entity_id)
/
CREATE INDEX audit_log_at_ix ON audit_log (at)
/

-- Retention policy as data: the package reads periods from here, the policy document is generated from the same rows.
CREATE TABLE retention_policy (
    category     VARCHAR2(24)   NOT NULL,
    retain_days  NUMBER(5)      NOT NULL,
    action       VARCHAR2(12)   NOT NULL,
    description  VARCHAR2(200)  NOT NULL,
    CONSTRAINT retention_policy_pk PRIMARY KEY (category),
    CONSTRAINT retention_policy_days_ck CHECK (retain_days >= 0),
    CONSTRAINT retention_policy_action_ck CHECK (action IN ('ANONYMISE', 'ERASE', 'DELETE', 'EXPIRE'))
)
/

CREATE TABLE retention_run (
    run_id               NUMBER GENERATED ALWAYS AS IDENTITY,
    started_at           TIMESTAMP     NOT NULL,
    finished_at          TIMESTAMP,
    as_of                TIMESTAMP     NOT NULL,
    triggered_by         VARCHAR2(64)  NOT NULL,
    tokens_expired       NUMBER        DEFAULT 0 NOT NULL,
    comments_erased      NUMBER        DEFAULT 0 NOT NULL,
    notes_erased         NUMBER        DEFAULT 0 NOT NULL,
    contacts_anonymised  NUMBER        DEFAULT 0 NOT NULL,
    held_open_case       NUMBER        DEFAULT 0 NOT NULL,
    audit_deleted        NUMBER        DEFAULT 0 NOT NULL,
    status               VARCHAR2(10)  DEFAULT 'RUNNING' NOT NULL,
    error_message        VARCHAR2(400),
    CONSTRAINT retention_run_pk PRIMARY KEY (run_id),
    CONSTRAINT retention_run_status_ck CHECK (status IN ('RUNNING', 'SUCCEEDED', 'FAILED'))
)
/

INSERT INTO retention_policy (category, retain_days, action, description)
VALUES ('SURVEY_TOKEN', 0, 'EXPIRE', 'Unused survey links: hash and queued token removed when the link expires')
/
INSERT INTO retention_policy (category, retain_days, action, description)
VALUES ('FREE_TEXT', 180, 'ERASE', 'Patient comments and resolved-case notes: ciphertext erased')
/
INSERT INTO retention_policy (category, retain_days, action, description)
VALUES ('CONTACT_PII', 365, 'ANONYMISE', 'Name, phone, patient reference and data key destroyed after the last activity')
/
INSERT INTO retention_policy (category, retain_days, action, description)
VALUES ('AUDIT_LOG', 730, 'DELETE', 'Personal-data access log rows deleted')
/
COMMIT
/
