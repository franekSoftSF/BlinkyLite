-- Second factor for operators: TOTP and single-use backup codes (0027, D-31).
--
-- A BlinkyLite login discloses PUKs and issues smart-card logon certificates
-- on behalf of anybody in the domain; an AD password alone - phishable, often
-- reused - is not enough in front of that. As in winch (ADR 0009), the second
-- factor is mandatory, not a setting.
--
-- The secret is sealed by the server with the KEK, like a PUK: the database
-- stores an envelope it cannot open, and neither role can SELECT it. The
-- backup codes are HMACs under a key derived from the same KEK version, so a
-- copy of the database is not a dictionary to brute-force offline.

CREATE TABLE operator_totp (
    operator_sid   text        PRIMARY KEY CHECK (operator_sid LIKE 'S-1-%'),
    operator_upn   text        NOT NULL CHECK (operator_upn <> ''),
    secret_envelope bytea      NOT NULL,
    kek_version    smallint    NOT NULL CHECK (kek_version > 0),
    created_at     timestamptz NOT NULL DEFAULT clock_timestamp(),
    -- Null until the operator has proven, with a code, that their
    -- authenticator holds the secret. An unconfirmed secret signs nobody in.
    confirmed_at   timestamptz NULL,
    -- The last 30-second step accepted. A code is refused unless its step is
    -- newer, so a code seen over a shoulder or in a proxy log is already spent.
    last_step      bigint      NULL
);

CREATE TABLE operator_backup_codes (
    id           bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    operator_sid text        NOT NULL REFERENCES operator_totp (operator_sid) ON DELETE CASCADE,
    code_hash    bytea       NOT NULL CHECK (length(code_hash) = 32),
    used_at      timestamptz NULL,
    UNIQUE (operator_sid, code_hash)
);

-- What the server may learn about an operator's TOTP without opening it.
-- Nothing is audited here: this is read on every sign-in, and the sign-in
-- itself is the audit event.
CREATE FUNCTION bl_totp_state(
    p_operator_sid text,
    p_actor_upn    text,
    p_actor_sid    text,
    p_actor_roles  text[],
    p_source_ip    inet)
RETURNS TABLE (secret_envelope bytea, kek_version smallint, confirmed boolean)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = blinkylite, pg_temp
AS $$
#variable_conflict use_column
BEGIN
    -- Only one's own: the ticket that reaches this function belongs to the
    -- person whose password was just checked.
    IF p_operator_sid IS DISTINCT FROM p_actor_sid THEN
        RAISE EXCEPTION USING
            ERRCODE = 'BL004',
            MESSAGE = 'error.forbidden',
            DETAIL  = 'an operator reads only their own second factor';
    END IF;

    RETURN QUERY
    SELECT t.secret_envelope, t.kek_version, t.confirmed_at IS NOT NULL
    FROM operator_totp t
    WHERE t.operator_sid = p_operator_sid;
END
$$;

-- A new, unconfirmed secret. Refused once a secret is confirmed: otherwise
-- anyone holding the password could quietly move the second factor to their
-- own phone. Replacing a confirmed one is bl_totp_reset, by somebody else.
CREATE FUNCTION bl_totp_begin(
    p_envelope    bytea,
    p_kek_version smallint,
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
    t operator_totp;
BEGIN
    PERFORM _bl_require_role(p_actor_roles, ARRAY['Admin', 'SecurityOfficer', 'Helpdesk']);

    IF p_actor_sid IS NULL OR p_envelope IS NULL OR p_kek_version IS NULL THEN
        RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'the operator SID, the envelope and the KEK version are required';
    END IF;

    SELECT * INTO t FROM operator_totp WHERE operator_sid = p_actor_sid FOR UPDATE;
    IF FOUND AND t.confirmed_at IS NOT NULL THEN
        RAISE EXCEPTION USING
            ERRCODE = 'BL007',
            MESSAGE = 'error.totp.already-configured',
            DETAIL  = format('%s already has a confirmed second factor', p_actor_sid);
    END IF;

    INSERT INTO operator_totp (operator_sid, operator_upn, secret_envelope, kek_version)
    VALUES (p_actor_sid, p_actor_upn, p_envelope, p_kek_version)
    ON CONFLICT (operator_sid) DO UPDATE
        SET operator_upn = EXCLUDED.operator_upn,
            secret_envelope = EXCLUDED.secret_envelope,
            kek_version = EXCLUDED.kek_version,
            created_at = clock_timestamp(),
            last_step = NULL;

    PERFORM _bl_write_audit('totp.setup-started', NULL, NULL, '{}'::jsonb,
        p_actor_upn, p_actor_sid, p_actor_roles, p_source_ip);
END
$$;

-- The first good code: the secret becomes the operator's second factor, the
-- backup codes are replaced, and this is the sign-in.
CREATE FUNCTION bl_totp_confirm(
    p_step         bigint,
    p_backup_codes bytea[],
    p_actor_upn    text,
    p_actor_sid    text,
    p_actor_roles  text[],
    p_source_ip    inet)
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = blinkylite, pg_temp
AS $$
DECLARE
    t operator_totp;
BEGIN
    PERFORM _bl_require_role(p_actor_roles, ARRAY['Admin', 'SecurityOfficer', 'Helpdesk']);

    IF p_step IS NULL OR coalesce(array_length(p_backup_codes, 1), 0) = 0 THEN
        RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'the step and the backup codes are required';
    END IF;

    SELECT * INTO t FROM operator_totp WHERE operator_sid = p_actor_sid FOR UPDATE;
    IF NOT FOUND THEN
        RAISE EXCEPTION USING
            ERRCODE = 'BL008',
            MESSAGE = 'error.totp.setup-required',
            DETAIL  = format('%s has not started a second factor', p_actor_sid);
    END IF;
    IF t.confirmed_at IS NOT NULL THEN
        RAISE EXCEPTION USING
            ERRCODE = 'BL007',
            MESSAGE = 'error.totp.already-configured',
            DETAIL  = format('%s already has a confirmed second factor', p_actor_sid);
    END IF;

    UPDATE operator_totp SET confirmed_at = clock_timestamp(), last_step = p_step
    WHERE operator_sid = p_actor_sid;

    DELETE FROM operator_backup_codes WHERE operator_sid = p_actor_sid;
    INSERT INTO operator_backup_codes (operator_sid, code_hash)
    SELECT p_actor_sid, h FROM unnest(p_backup_codes) AS h;

    PERFORM _bl_write_audit('totp.enrolled', NULL, NULL,
        jsonb_build_object('backup_codes', array_length(p_backup_codes, 1)),
        p_actor_upn, p_actor_sid, p_actor_roles, p_source_ip);
    PERFORM _bl_write_audit('auth.login', NULL, NULL,
        jsonb_build_object('roles', to_jsonb(p_actor_roles), 'second_factor', 'totp'),
        p_actor_upn, p_actor_sid, p_actor_roles, p_source_ip);
END
$$;

-- A code the server has checked against the secret. The step is taken here,
-- under the row lock, so that two requests carrying the same code cannot
-- both win: the second sees the first one's step and is refused.
CREATE FUNCTION bl_totp_accept(
    p_step        bigint,
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
    t operator_totp;
BEGIN
    PERFORM _bl_require_role(p_actor_roles, ARRAY['Admin', 'SecurityOfficer', 'Helpdesk']);

    SELECT * INTO t FROM operator_totp WHERE operator_sid = p_actor_sid FOR UPDATE;
    IF NOT FOUND OR t.confirmed_at IS NULL THEN
        RAISE EXCEPTION USING
            ERRCODE = 'BL008',
            MESSAGE = 'error.totp.setup-required',
            DETAIL  = format('%s has no confirmed second factor', p_actor_sid);
    END IF;

    IF p_step IS NULL OR (t.last_step IS NOT NULL AND p_step <= t.last_step) THEN
        RAISE EXCEPTION USING
            ERRCODE = 'BL006',
            MESSAGE = 'error.totp.invalid',
            DETAIL  = format('step %s is not newer than %s', p_step, t.last_step);
    END IF;

    UPDATE operator_totp SET last_step = p_step WHERE operator_sid = p_actor_sid;

    PERFORM _bl_write_audit('auth.login', NULL, NULL,
        jsonb_build_object('roles', to_jsonb(p_actor_roles), 'second_factor', 'totp'),
        p_actor_upn, p_actor_sid, p_actor_roles, p_source_ip);
END
$$;

-- A backup code instead of the authenticator. Spent by the UPDATE itself, so
-- the same code sent twice at once signs in once. Returns how many are left,
-- because an operator down to their last code should be told.
CREATE FUNCTION bl_totp_backup_use(
    p_code_hash   bytea,
    p_actor_upn   text,
    p_actor_sid   text,
    p_actor_roles text[],
    p_source_ip   inet)
RETURNS integer
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = blinkylite, pg_temp
AS $$
DECLARE
    v_left integer;
BEGIN
    PERFORM _bl_require_role(p_actor_roles, ARRAY['Admin', 'SecurityOfficer', 'Helpdesk']);

    PERFORM 1 FROM operator_totp
    WHERE operator_sid = p_actor_sid AND confirmed_at IS NOT NULL
    FOR UPDATE;
    IF NOT FOUND THEN
        RAISE EXCEPTION USING
            ERRCODE = 'BL008',
            MESSAGE = 'error.totp.setup-required',
            DETAIL  = format('%s has no confirmed second factor', p_actor_sid);
    END IF;

    UPDATE operator_backup_codes SET used_at = clock_timestamp()
    WHERE operator_sid = p_actor_sid AND code_hash = p_code_hash AND used_at IS NULL;
    IF NOT FOUND THEN
        RAISE EXCEPTION USING
            ERRCODE = 'BL006',
            MESSAGE = 'error.totp.invalid',
            DETAIL  = 'no unused backup code matches';
    END IF;

    SELECT count(*) INTO v_left FROM operator_backup_codes
    WHERE operator_sid = p_actor_sid AND used_at IS NULL;

    PERFORM _bl_write_audit('auth.login', NULL, NULL,
        jsonb_build_object('roles', to_jsonb(p_actor_roles), 'second_factor', 'backup-code',
                           'backup_codes_left', v_left),
        p_actor_upn, p_actor_sid, p_actor_roles, p_source_ip);

    RETURN v_left;
END
$$;

-- A lost phone and lost backup codes. Only an Admin, and never for
-- themselves: an Admin who could reset their own factor would make the
-- factor a formality for the one role that most needs it. The operator sets
-- up a new one at their next sign-in.
CREATE FUNCTION bl_totp_reset(
    p_operator_sid text,
    p_reason       text,
    p_actor_upn    text,
    p_actor_sid    text,
    p_actor_roles  text[],
    p_source_ip    inet)
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = blinkylite, pg_temp
AS $$
DECLARE
    t operator_totp;
BEGIN
    PERFORM _bl_require_role(p_actor_roles, ARRAY['Admin']);

    IF p_operator_sid IS NOT DISTINCT FROM p_actor_sid THEN
        RAISE EXCEPTION USING
            ERRCODE = 'BL004',
            MESSAGE = 'error.forbidden',
            DETAIL  = 'an Admin cannot reset their own second factor';
    END IF;

    IF p_reason IS NULL OR length(btrim(p_reason)) < 5 THEN
        RAISE EXCEPTION USING
            ERRCODE = 'BL005',
            MESSAGE = 'error.reason.required',
            DETAIL  = 'a reset needs a reason of at least 5 characters';
    END IF;

    SELECT * INTO t FROM operator_totp WHERE operator_sid = p_operator_sid FOR UPDATE;
    IF NOT FOUND THEN
        RAISE EXCEPTION USING
            ERRCODE = 'BL002',
            MESSAGE = 'error.not-found',
            DETAIL  = format('%s has no second factor', p_operator_sid);
    END IF;

    DELETE FROM operator_totp WHERE operator_sid = p_operator_sid;

    PERFORM _bl_write_audit('totp.reset', NULL, NULL,
        jsonb_build_object('operator_sid', p_operator_sid, 'operator_upn', t.operator_upn,
                           'reason', btrim(p_reason)),
        p_actor_upn, p_actor_sid, p_actor_roles, p_source_ip);
END
$$;

REVOKE EXECUTE ON FUNCTION
    bl_totp_state, bl_totp_begin, bl_totp_confirm, bl_totp_accept, bl_totp_backup_use, bl_totp_reset
FROM PUBLIC;

-- No SELECT on either table for anybody: the envelope and the hashes are
-- reached through bl_totp_state and nothing else.
GRANT EXECUTE ON FUNCTION
    bl_totp_state, bl_totp_begin, bl_totp_confirm, bl_totp_accept, bl_totp_backup_use, bl_totp_reset
TO blinkylite_app;
