-- Outbox claiming for competing dispatcher instances.
-- Oracle rejects FOR UPDATE combined with FETCH FIRST (ORA-02014), and ROWNUM is applied before SKIP LOCKED skips
-- rows, so "WHERE ROWNUM <= n FOR UPDATE SKIP LOCKED" can return fewer rows than are available. The correct
-- pattern is to open a FOR UPDATE SKIP LOCKED cursor and fetch with LIMIT: rows are locked as they are fetched,
-- rows locked by another instance are skipped, and exactly up to p_limit rows are claimed.
CREATE OR REPLACE PACKAGE outbox_pkg AUTHID DEFINER AS
    -- Claims up to p_limit due messages: PENDING rows whose next_attempt_at has passed, plus IN_FLIGHT rows whose
    -- lease expired (a dispatcher crashed mid-send). Claimed rows carry p_claim_token; the caller reads them by it.
    -- No COMMIT here: the caller's statement auto-commits, or commits with its own transaction.
    PROCEDURE claim(
        p_limit         IN PLS_INTEGER,
        p_lease_seconds IN PLS_INTEGER,
        p_now           IN TIMESTAMP,
        p_claim_token   IN RAW,
        p_claimed       OUT PLS_INTEGER);
END outbox_pkg;
/

CREATE OR REPLACE PACKAGE BODY outbox_pkg AS
    PROCEDURE claim(
        p_limit         IN PLS_INTEGER,
        p_lease_seconds IN PLS_INTEGER,
        p_now           IN TIMESTAMP,
        p_claim_token   IN RAW,
        p_claimed       OUT PLS_INTEGER)
    IS
        TYPE t_ids IS TABLE OF outbox_message.message_id%TYPE;
        l_ids t_ids;

        -- The CASE expression matches outbox_due_ix exactly, so only claimable rows are visited.
        CURSOR c_due IS
            SELECT message_id
              FROM outbox_message
             WHERE CASE WHEN status IN ('PENDING', 'IN_FLIGHT') THEN next_attempt_at END <= p_now
               AND (status = 'PENDING' OR lease_until < p_now)
             ORDER BY next_attempt_at
               FOR UPDATE SKIP LOCKED;
    BEGIN
        IF p_limit < 1 OR p_lease_seconds < 1 THEN
            RAISE_APPLICATION_ERROR(-20001, 'p_limit and p_lease_seconds must be positive');
        END IF;

        OPEN c_due;
        FETCH c_due BULK COLLECT INTO l_ids LIMIT p_limit;
        CLOSE c_due; -- row locks are held until the transaction ends, not until the cursor closes

        FORALL i IN 1 .. l_ids.COUNT
            UPDATE outbox_message
               SET status = 'IN_FLIGHT',
                   lease_until = p_now + NUMTODSINTERVAL(p_lease_seconds, 'SECOND'),
                   claim_token = p_claim_token
             WHERE message_id = l_ids(i);

        p_claimed := l_ids.COUNT;
    END claim;
END outbox_pkg;
/
