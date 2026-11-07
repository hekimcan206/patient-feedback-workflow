-- KVKK periodic destruction (Regulation on Erasure, Destruction or Anonymisation of Personal Data, art. 11:
-- the interval is set in the controller's retention policy and may not exceed six months). This package applies the
-- periods stored in retention_policy; DBMS_SCHEDULER runs it nightly, well inside the six-month ceiling.
--
-- Design:
--  * Idempotent and restartable: every step selects only rows that still need work, so a failed or repeated run
--    simply continues. Contacts are processed in batches with a COMMIT per batch; each batch re-queries instead
--    of fetching across commits (avoids ORA-01555 on a long-open cursor).
--  * Legal hold: a contact with an unresolved case is not anonymised (the case handler still needs to call back);
--    it is counted in held_open_case and picked up by the first run after the case is resolved.
--  * Crypto-shredding: destroying wrapped_dek makes every remaining copy of the contact's ciphertext unreadable,
--    including copies in backups that this job cannot reach.
--  * Statistics survive: scores, departments and timestamps are not personal data once the contact is anonymised.
CREATE OR REPLACE PACKAGE retention_pkg AUTHID DEFINER AS
    c_job_name CONSTANT VARCHAR2(30) := 'PF_RETENTION_NIGHTLY';

    PROCEDURE run(
        p_as_of        IN  TIMESTAMP DEFAULT NULL,
        p_triggered_by IN  VARCHAR2  DEFAULT 'MANUAL',
        p_batch_size   IN  PLS_INTEGER DEFAULT 500,
        p_run_id       OUT NUMBER);

    -- Entry point of the scheduler job.
    PROCEDURE run_scheduled;

    -- (Re)creates the nightly job. Calendar syntax, e.g. 'FREQ=DAILY;BYHOUR=2;BYMINUTE=30'.
    PROCEDURE ensure_job(
        p_repeat_interval IN VARCHAR2 DEFAULT 'FREQ=DAILY;BYHOUR=2;BYMINUTE=30',
        p_enabled         IN BOOLEAN  DEFAULT TRUE);
END retention_pkg;
/

CREATE OR REPLACE PACKAGE BODY retention_pkg AS
    c_actor   CONSTANT VARCHAR2(20) := 'RETENTION_JOB';
    c_purpose CONSTANT VARCHAR2(40) := 'kvkk-periodic-destruction';

    FUNCTION cutoff(p_category IN VARCHAR2, p_as_of IN TIMESTAMP) RETURN TIMESTAMP IS
        l_days retention_policy.retain_days%TYPE;
    BEGIN
        SELECT retain_days INTO l_days FROM retention_policy WHERE category = p_category;
        RETURN p_as_of - NUMTODSINTERVAL(l_days, 'DAY');
    EXCEPTION
        WHEN NO_DATA_FOUND THEN
            RAISE_APPLICATION_ERROR(-20010, 'retention_policy has no row for ' || p_category);
    END cutoff;

    PROCEDURE run(
        p_as_of        IN  TIMESTAMP DEFAULT NULL,
        p_triggered_by IN  VARCHAR2  DEFAULT 'MANUAL',
        p_batch_size   IN  PLS_INTEGER DEFAULT 500,
        p_run_id       OUT NUMBER)
    IS
        TYPE t_ids IS TABLE OF contact.contact_id%TYPE;
        l_ids      t_ids;
        l_now      CONSTANT TIMESTAMP := SYS_EXTRACT_UTC(SYSTIMESTAMP);
        l_as_of    CONSTANT TIMESTAMP := NVL(p_as_of, l_now);
        l_cutoff   TIMESTAMP;
        l_tokens   NUMBER := 0;
        l_comments NUMBER := 0;
        l_notes    NUMBER := 0;
        l_contacts NUMBER := 0;
        l_held     NUMBER := 0;
        l_audit    NUMBER := 0;
        l_error    VARCHAR2(400);
    BEGIN
        IF p_batch_size < 1 THEN
            RAISE_APPLICATION_ERROR(-20011, 'p_batch_size must be positive');
        END IF;

        INSERT INTO retention_run (started_at, as_of, triggered_by)
        VALUES (l_now, l_as_of, SUBSTR(p_triggered_by, 1, 64))
        RETURNING run_id INTO p_run_id;
        COMMIT;

        -- 1. Expired survey links: the link stops working and the queued (encrypted) token is destroyed.
        UPDATE outbox_message
           SET token_ct = NULL,
               status = CASE WHEN status IN ('PENDING', 'IN_FLIGHT') THEN 'CANCELLED' ELSE status END
         WHERE survey_id IN (SELECT survey_id FROM survey
                              WHERE CASE WHEN status IN ('SCHEDULED', 'SENT') THEN expires_at END < l_as_of)
           AND (token_ct IS NOT NULL OR status IN ('PENDING', 'IN_FLIGHT'));
        UPDATE survey
           SET status = 'EXPIRED', token_hash = NULL
         WHERE CASE WHEN status IN ('SCHEDULED', 'SENT') THEN expires_at END < l_as_of;
        l_tokens := SQL%ROWCOUNT;
        COMMIT;

        -- 2. Free text past its period: comments, and notes of resolved cases.
        l_cutoff := cutoff('FREE_TEXT', l_as_of);
        UPDATE survey_response
           SET comment_ct = NULL
         WHERE comment_ct IS NOT NULL
           AND submitted_at < l_cutoff;
        l_comments := SQL%ROWCOUNT;
        UPDATE case_event e
           SET note_ct = NULL
         WHERE note_ct IS NOT NULL
           AND at < l_cutoff
           AND EXISTS (SELECT 1 FROM feedback_case fc WHERE fc.case_id = e.case_id AND fc.status = 'RESOLVED');
        l_notes := SQL%ROWCOUNT;
        COMMIT;

        -- 3. Contacts past their period, in batches. The CASE predicate uses contact_retention_ix (live rows only).
        l_cutoff := cutoff('CONTACT_PII', l_as_of);
        SELECT COUNT(*)
          INTO l_held
          FROM contact c
         WHERE CASE WHEN c.anonymised_at IS NULL THEN c.last_activity_at END < l_cutoff
           AND EXISTS (SELECT 1 FROM feedback_case fc WHERE fc.contact_id = c.contact_id AND fc.status <> 'RESOLVED');

        LOOP
            SELECT c.contact_id
              BULK COLLECT INTO l_ids
              FROM contact c
             WHERE CASE WHEN c.anonymised_at IS NULL THEN c.last_activity_at END < l_cutoff
               AND NOT EXISTS (SELECT 1 FROM feedback_case fc WHERE fc.contact_id = c.contact_id AND fc.status <> 'RESOLVED')
             FETCH FIRST p_batch_size ROWS ONLY;
            EXIT WHEN l_ids.COUNT = 0;

            FORALL i IN 1 .. l_ids.COUNT
                UPDATE contact
                   SET patient_ref_bidx = NULL, phone_bidx = NULL, full_name_ct = NULL, phone_ct = NULL,
                       wrapped_dek = NULL, kek_id = NULL, anonymised_at = l_now
                 WHERE contact_id = l_ids(i);

            -- The data key is gone, so remaining ciphertext is already unreadable; it is erased anyway so nothing
            -- that looks like personal data is left in the row.
            FORALL i IN 1 .. l_ids.COUNT
                UPDATE survey_response
                   SET comment_ct = NULL
                 WHERE comment_ct IS NOT NULL
                   AND survey_id IN (SELECT survey_id FROM survey WHERE contact_id = l_ids(i));
            FORALL i IN 1 .. l_ids.COUNT
                UPDATE case_event
                   SET note_ct = NULL
                 WHERE note_ct IS NOT NULL
                   AND case_id IN (SELECT case_id FROM feedback_case WHERE contact_id = l_ids(i));
            FORALL i IN 1 .. l_ids.COUNT
                UPDATE outbox_message
                   SET token_ct = NULL
                 WHERE token_ct IS NOT NULL
                   AND survey_id IN (SELECT survey_id FROM survey WHERE contact_id = l_ids(i));
            FORALL i IN 1 .. l_ids.COUNT
                UPDATE survey
                   SET token_hash = NULL
                 WHERE contact_id = l_ids(i)
                   AND token_hash IS NOT NULL;
            FORALL i IN 1 .. l_ids.COUNT
                INSERT INTO audit_log (actor, action, entity, entity_id, fields, purpose)
                VALUES (c_actor, 'PII_ANONYMISE', 'contact', l_ids(i), 'patient_ref,full_name,phone,data_key,free_text', c_purpose);

            l_contacts := l_contacts + l_ids.COUNT;
            COMMIT;
        END LOOP;

        -- 4. The access log itself has a retention period.
        l_cutoff := cutoff('AUDIT_LOG', l_as_of);
        DELETE FROM audit_log WHERE at < l_cutoff;
        l_audit := SQL%ROWCOUNT;

        UPDATE retention_run
           SET finished_at = SYS_EXTRACT_UTC(SYSTIMESTAMP), status = 'SUCCEEDED',
               tokens_expired = l_tokens, comments_erased = l_comments, notes_erased = l_notes,
               contacts_anonymised = l_contacts, held_open_case = l_held, audit_deleted = l_audit
         WHERE run_id = p_run_id;
        COMMIT;
    EXCEPTION
        WHEN OTHERS THEN
            l_error := SUBSTR(SQLERRM, 1, 400);
            ROLLBACK;
            IF p_run_id IS NOT NULL THEN
                UPDATE retention_run
                   SET finished_at = SYS_EXTRACT_UTC(SYSTIMESTAMP), status = 'FAILED', error_message = l_error,
                       tokens_expired = l_tokens, comments_erased = l_comments, notes_erased = l_notes,
                       contacts_anonymised = l_contacts, held_open_case = l_held
                 WHERE run_id = p_run_id;
                COMMIT;
            END IF;
            RAISE;
    END run;

    PROCEDURE run_scheduled IS
        l_run_id NUMBER;
    BEGIN
        run(p_as_of => NULL, p_triggered_by => 'SCHEDULER', p_run_id => l_run_id);
    END run_scheduled;

    PROCEDURE ensure_job(
        p_repeat_interval IN VARCHAR2 DEFAULT 'FREQ=DAILY;BYHOUR=2;BYMINUTE=30',
        p_enabled         IN BOOLEAN  DEFAULT TRUE)
    IS
        e_unknown_job EXCEPTION;
        PRAGMA EXCEPTION_INIT(e_unknown_job, -27475);
    BEGIN
        BEGIN
            DBMS_SCHEDULER.DROP_JOB(job_name => c_job_name, force => TRUE);
        EXCEPTION
            WHEN e_unknown_job THEN NULL;
        END;

        DBMS_SCHEDULER.CREATE_JOB(
            job_name        => c_job_name,
            job_type        => 'STORED_PROCEDURE',
            job_action      => 'RETENTION_PKG.RUN_SCHEDULED',
            start_date      => SYSTIMESTAMP,
            repeat_interval => p_repeat_interval,
            enabled         => FALSE,
            comments        => 'KVKK periodic destruction: anonymise or erase personal data past its retention period');
        -- A failed night is retried by the next run (the package is idempotent); keep the history for audit.
        DBMS_SCHEDULER.SET_ATTRIBUTE(c_job_name, 'logging_level', DBMS_SCHEDULER.LOGGING_FULL);
        IF p_enabled THEN
            DBMS_SCHEDULER.ENABLE(c_job_name);
        END IF;
    END ensure_job;
END retention_pkg;
/
