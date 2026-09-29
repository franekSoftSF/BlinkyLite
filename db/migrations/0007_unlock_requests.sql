-- Remote PIN unblock: the person reads out a code, an operator approves it,
-- and their machine fetches the PUK itself (0057, D-37).
--
-- Why the PUK travels over TLS and not over the telephone: unblocking needs
-- the PUK itself - the card has no challenge-response that could stand in for
-- it - so the only question is who hears it. This way nobody does, and the
-- three moments that matter are on record: who asked, who approved and with
-- what reason, and whether the card accepted it.
--
-- The state machine is here, not in the server, for the same reason as every
-- other write: two requests arriving at once must not both win, and a PUK
-- handed out twice is a PUK handed out once too often.

CREATE TABLE unlock_requests (
    id            uuid        PRIMARY KEY,
    card_serial   bigint      NOT NULL REFERENCES cards (serial),

    -- What the person reads out. Short enough for a telephone, from an
    -- alphabet without characters people confuse; unique only among the
    -- requests that are still waiting, which is when it is used.
    code          text        NOT NULL CHECK (code <> ''),

    -- SHA-256 of a secret the asking application made up and keeps. Knowing
    -- the code is enough to be approved, never enough to collect the PUK:
    -- that needs this secret, so an eavesdropper with the code gets nothing.
    secret_hash   bytea       NOT NULL CHECK (length(secret_hash) = 32),

    workstation   text        NOT NULL DEFAULT '',
    source_ip     inet        NULL,

    state         text        NOT NULL DEFAULT 'Pending'
                              CHECK (state IN ('Pending', 'Approved', 'Delivered', 'Completed', 'Failed', 'Refused', 'Expired')),
    created_at    timestamptz NOT NULL DEFAULT clock_timestamp(),
    expires_at    timestamptz NOT NULL,

    approver_upn  text        NULL,
    approver_sid  text        NULL,
    reason        text        NULL,
    decided_at    timestamptz NULL,
    delivered_at  timestamptz NULL,
    finished_at   timestamptz NULL,
    error         text        NULL
);

-- One waiting request per card: a second call about the same key replaces the
-- first, so an operator never has to guess which of two codes is live.
CREATE UNIQUE INDEX unlock_requests_pending_card ON unlock_requests (card_serial) WHERE state = 'Pending';
CREATE UNIQUE INDEX unlock_requests_pending_code ON unlock_requests (code) WHERE state = 'Pending';
CREATE INDEX unlock_requests_created ON unlock_requests (created_at DESC);

-- Waiting longer than this is not waiting, it is a code somebody wrote down
-- yesterday.
CREATE FUNCTION _bl_unlock_expire()
RETURNS void
LANGUAGE sql
SET search_path = blinkylite, pg_temp
AS $$
    UPDATE unlock_requests SET state = 'Expired'
    WHERE state IN ('Pending', 'Approved') AND expires_at < clock_timestamp();
$$;

-- The asking application, with nobody signed in: it knows a card serial and
-- nothing else. It learns only that somebody may now be asked to approve.
CREATE FUNCTION bl_unlock_request(
    p_id          uuid,
    p_card_serial bigint,
    p_code        text,
    p_secret_hash bytea,
    p_workstation text,
    p_minutes     integer,
    p_actor_upn   text,
    p_actor_sid   text,
    p_actor_roles text[],
    p_source_ip   inet)
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = blinkylite, pg_temp
AS $$
BEGIN
    PERFORM _bl_unlock_expire();

    -- A card this server never issued has no PUK here, and saying so is not a
    -- leak: whoever holds the card can read its serial anyway.
    PERFORM 1 FROM card_secrets
    WHERE card_serial = p_card_serial AND state = 'Active' AND puk_envelope IS NOT NULL;
    IF NOT FOUND THEN
        RAISE EXCEPTION USING
            ERRCODE = 'BL002',
            MESSAGE = 'error.not-found',
            DETAIL  = format('card %s has no active PUK', p_card_serial);
    END IF;

    UPDATE unlock_requests SET state = 'Expired'
    WHERE card_serial = p_card_serial AND state IN ('Pending', 'Approved');

    INSERT INTO unlock_requests (id, card_serial, code, secret_hash, workstation, source_ip, expires_at)
    VALUES (p_id, p_card_serial, p_code, p_secret_hash, coalesce(p_workstation, ''), p_source_ip,
            clock_timestamp() + make_interval(mins => p_minutes));

    PERFORM _bl_write_audit('unlock.requested', p_card_serial, NULL,
        jsonb_build_object('request', p_id, 'workstation', coalesce(p_workstation, '')),
        p_actor_upn, p_actor_sid, p_actor_roles, p_source_ip);
END
$$;

-- What an operator sees before deciding: which key, whose it is, from where.
-- No secret, no envelope - a list is not a disclosure.
CREATE FUNCTION bl_unlock_waiting(
    p_actor_upn   text,
    p_actor_sid   text,
    p_actor_roles text[],
    p_source_ip   inet)
RETURNS TABLE (
    id                  uuid,
    code                text,
    card_serial         bigint,
    workstation         text,
    source_ip           inet,
    created_at          timestamptz,
    expires_at          timestamptz,
    target_display_name text,
    target_sam          text)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = blinkylite, pg_temp
AS $$
#variable_conflict use_column
BEGIN
    PERFORM _bl_require_role(p_actor_roles, ARRAY['Admin', 'SecurityOfficer', 'Helpdesk']);
    PERFORM _bl_unlock_expire();

    RETURN QUERY
    SELECT r.id, r.code, r.card_serial, r.workstation, r.source_ip, r.created_at, r.expires_at,
           i.target_display_name, i.target_sam
    FROM unlock_requests r
    LEFT JOIN cards c ON c.serial = r.card_serial
    LEFT JOIN issuances i ON i.id = c.current_issuance_id
    WHERE r.state = 'Pending'
    ORDER BY r.created_at;
END
$$;

-- The decision. The same roles that may disclose a PUK the ordinary way, and
-- the same rule about reasons: an approval without one explains nothing in
-- three months.
CREATE FUNCTION bl_unlock_decide(
    p_id          uuid,
    p_approve     boolean,
    p_reason      text,
    p_actor_upn   text,
    p_actor_sid   text,
    p_actor_roles text[],
    p_source_ip   inet)
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = blinkylite, pg_temp
AS $$
DECLARE
    r unlock_requests;
BEGIN
    PERFORM _bl_require_role(p_actor_roles, ARRAY['Admin', 'SecurityOfficer', 'Helpdesk']);
    PERFORM _bl_unlock_expire();

    IF p_reason IS NULL OR length(btrim(p_reason)) < 5 THEN
        RAISE EXCEPTION USING
            ERRCODE = 'BL005',
            MESSAGE = 'error.reason.required',
            DETAIL  = 'a decision needs a reason of at least 5 characters';
    END IF;

    SELECT * INTO r FROM unlock_requests WHERE id = p_id FOR UPDATE;
    IF NOT FOUND THEN
        RAISE EXCEPTION USING ERRCODE = 'BL002', MESSAGE = 'error.not-found', DETAIL = format('request %s', p_id);
    END IF;
    IF r.state <> 'Pending' THEN
        RAISE EXCEPTION USING
            ERRCODE = 'BL001',
            MESSAGE = 'error.unlock.invalid-state',
            DETAIL  = format('request %s is %s', p_id, r.state);
    END IF;

    UPDATE unlock_requests
    SET state = CASE WHEN p_approve THEN 'Approved' ELSE 'Refused' END,
        approver_upn = p_actor_upn,
        approver_sid = p_actor_sid,
        reason = btrim(p_reason),
        decided_at = clock_timestamp()
    WHERE id = p_id;

    PERFORM _bl_write_audit(CASE WHEN p_approve THEN 'unlock.approved' ELSE 'unlock.refused' END,
        r.card_serial, NULL,
        jsonb_build_object('request', p_id, 'reason', btrim(p_reason), 'workstation', r.workstation),
        p_actor_upn, p_actor_sid, p_actor_roles, p_source_ip);
END
$$;

-- Where the application learns how far it got, and - once, after an approval -
-- collects the envelope. Delivering flips the state in the same statement, so
-- a second call gets nothing.
CREATE FUNCTION bl_unlock_collect(
    p_id          uuid,
    p_secret_hash bytea,
    p_actor_upn   text,
    p_actor_sid   text,
    p_actor_roles text[],
    p_source_ip   inet)
RETURNS TABLE (state text, card_serial bigint, issuance_id uuid, envelope bytea, kek_version smallint)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = blinkylite, pg_temp
AS $$
#variable_conflict use_column
DECLARE
    r unlock_requests;
    s card_secrets;
BEGIN
    PERFORM _bl_unlock_expire();

    SELECT * INTO r FROM unlock_requests WHERE id = p_id FOR UPDATE;

    -- A wrong secret is answered exactly like an unknown request: the id alone
    -- must not tell anybody whether a request exists.
    IF NOT FOUND OR r.secret_hash <> p_secret_hash THEN
        RAISE EXCEPTION USING ERRCODE = 'BL002', MESSAGE = 'error.not-found', DETAIL = format('request %s', p_id);
    END IF;

    IF r.state <> 'Approved' THEN
        RETURN QUERY SELECT r.state, r.card_serial, NULL::uuid, NULL::bytea, NULL::smallint;
        RETURN;
    END IF;

    SELECT * INTO s FROM card_secrets
    WHERE card_serial = r.card_serial AND state = 'Active' AND puk_envelope IS NOT NULL
    FOR UPDATE;
    IF NOT FOUND THEN
        RAISE EXCEPTION USING
            ERRCODE = 'BL002',
            MESSAGE = 'error.not-found',
            DETAIL  = format('card %s has no active PUK', r.card_serial);
    END IF;

    UPDATE unlock_requests SET state = 'Delivered', delivered_at = clock_timestamp() WHERE id = p_id;
    UPDATE card_secrets SET puk_disclosed_count = puk_disclosed_count + 1 WHERE id = s.id;

    -- The disclosure event an ordinary reveal writes, with the request and the
    -- operator who approved it instead of a person reading it out.
    PERFORM _bl_write_audit('puk.disclosed', r.card_serial, s.issuance_id,
        jsonb_build_object('request', p_id, 'reason', r.reason, 'secret_id', s.id,
                           'approved_by', r.approver_upn, 'remote', true),
        p_actor_upn, p_actor_sid, p_actor_roles, p_source_ip);

    RETURN QUERY SELECT 'Delivered'::text, r.card_serial, s.issuance_id, s.puk_envelope, s.kek_version;
END
$$;

-- How it ended at the card. Written by the same application, with the same
-- secret; a request nobody finishes stays Delivered and says so in the log.
CREATE FUNCTION bl_unlock_finish(
    p_id          uuid,
    p_secret_hash bytea,
    p_ok          boolean,
    p_error       text,
    p_actor_upn   text,
    p_actor_sid   text,
    p_actor_roles text[],
    p_source_ip   inet)
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = blinkylite, pg_temp
AS $$
DECLARE
    r unlock_requests;
BEGIN
    SELECT * INTO r FROM unlock_requests WHERE id = p_id FOR UPDATE;
    IF NOT FOUND OR r.secret_hash <> p_secret_hash THEN
        RAISE EXCEPTION USING ERRCODE = 'BL002', MESSAGE = 'error.not-found', DETAIL = format('request %s', p_id);
    END IF;
    IF r.state <> 'Delivered' THEN
        RAISE EXCEPTION USING
            ERRCODE = 'BL001',
            MESSAGE = 'error.unlock.invalid-state',
            DETAIL  = format('request %s is %s', p_id, r.state);
    END IF;

    UPDATE unlock_requests
    SET state = CASE WHEN p_ok THEN 'Completed' ELSE 'Failed' END,
        finished_at = clock_timestamp(),
        error = CASE WHEN p_ok THEN NULL ELSE left(coalesce(p_error, ''), 500) END
    WHERE id = p_id;

    PERFORM _bl_write_audit(CASE WHEN p_ok THEN 'unlock.completed' ELSE 'unlock.failed' END,
        r.card_serial, NULL,
        jsonb_build_object('request', p_id, 'approved_by', r.approver_upn,
                           'error', CASE WHEN p_ok THEN NULL ELSE left(coalesce(p_error, ''), 500) END),
        p_actor_upn, p_actor_sid, p_actor_roles, p_source_ip);
END
$$;

REVOKE EXECUTE ON FUNCTION
    bl_unlock_request, bl_unlock_waiting, bl_unlock_decide, bl_unlock_collect, bl_unlock_finish
FROM PUBLIC;

GRANT EXECUTE ON FUNCTION
    bl_unlock_request, bl_unlock_waiting, bl_unlock_decide, bl_unlock_collect, bl_unlock_finish
TO blinkylite_app;

-- The table is reached through the functions; a SELECT would hand out the
-- codes of every request waiting right now.
