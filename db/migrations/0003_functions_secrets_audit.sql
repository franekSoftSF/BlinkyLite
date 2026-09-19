-- Secrets and the free-standing audit entry.

-- The only way to a card's PUK or management key. The audit event is written
-- in the same transaction that returns the envelope: there is no envelope
-- without a record of who asked and why. Returns the issuance id as well,
-- because it is part of the envelope's AAD.
CREATE FUNCTION bl_secret_disclose(
    p_card_serial bigint,
    p_kind        text,
    p_reason      text,
    p_actor_upn   text,
    p_actor_sid   text,
    p_actor_roles text[],
    p_source_ip   inet)
RETURNS TABLE (issuance_id uuid, envelope bytea, kek_version smallint)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = blinkylite, pg_temp
AS $$
#variable_conflict use_column
DECLARE
    s card_secrets;
BEGIN
    IF p_kind IS NULL OR p_kind NOT IN ('puk', 'mgmt-key') THEN
        RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = format('unknown secret kind %L', p_kind);
    END IF;

    -- The management key opens the card for writing; only Admin sees it.
    IF p_kind = 'mgmt-key' THEN
        PERFORM _bl_require_role(p_actor_roles, ARRAY['Admin']);
    ELSE
        PERFORM _bl_require_role(p_actor_roles, ARRAY['Admin', 'SecurityOfficer', 'Helpdesk']);
    END IF;

    IF p_reason IS NULL OR length(btrim(p_reason)) < 5 THEN
        RAISE EXCEPTION USING
            ERRCODE = 'BL005',
            MESSAGE = 'error.reason.required',
            DETAIL  = 'a disclosure needs a reason of at least 5 characters';
    END IF;

    SELECT * INTO s FROM card_secrets
    WHERE card_serial = p_card_serial AND state = 'Active'
    FOR UPDATE;
    IF NOT FOUND OR (p_kind = 'puk' AND s.puk_envelope IS NULL) THEN
        RAISE EXCEPTION USING
            ERRCODE = 'BL002',
            MESSAGE = 'error.not-found',
            DETAIL  = format('card %s has no active %s', p_card_serial, p_kind);
    END IF;

    IF p_kind = 'puk' THEN
        UPDATE card_secrets SET puk_disclosed_count = puk_disclosed_count + 1 WHERE id = s.id;
    END IF;

    PERFORM _bl_write_audit(p_kind || '.disclosed', p_card_serial, s.issuance_id,
        jsonb_build_object('reason', btrim(p_reason), 'secret_id', s.id),
        p_actor_upn, p_actor_sid, p_actor_roles, p_source_ip);

    RETURN QUERY SELECT
        s.issuance_id,
        CASE WHEN p_kind = 'puk' THEN s.puk_envelope ELSE s.mgmt_key_envelope END,
        s.kek_version;
END
$$;

-- Management keys that may be on the card: the Active one first, then
-- reservations newer than it, newest first (a card whose issuance crashed
-- after SET MANAGEMENT KEY holds one of those).
CREATE FUNCTION _bl_mgmt_key_candidates(p_card_serial bigint)
RETURNS TABLE (
    ord                bigint,
    secret_id          uuid,
    issuance_id        uuid,
    secret_state       text,
    envelope           bytea,
    mgmt_key_algorithm smallint,
    kek_version        smallint)
LANGUAGE sql
STABLE
SET search_path = blinkylite, pg_temp
AS $$
    SELECT row_number() OVER (ORDER BY (c.state = 'Active') DESC, c.created_at DESC),
           c.id, c.issuance_id, c.state, c.mgmt_key_envelope, c.mgmt_key_algorithm, c.kek_version
    FROM card_secrets c
    WHERE c.card_serial = p_card_serial
      AND (c.state = 'Active'
           OR (c.state = 'Reserved'
               AND c.created_at > coalesce(
                   (SELECT a.created_at FROM card_secrets a
                    WHERE a.card_serial = p_card_serial AND a.state = 'Active'),
                   '-infinity'::timestamptz)));
$$;

-- Handing a workstation a management key is a disclosure too, so it is
-- audited, with the ids of every envelope handed out.
CREATE FUNCTION bl_mgmt_key_candidates(
    p_card_serial bigint,
    p_actor_upn   text,
    p_actor_sid   text,
    p_actor_roles text[],
    p_source_ip   inet)
RETURNS TABLE (
    secret_id          uuid,
    issuance_id        uuid,
    secret_state       text,
    envelope           bytea,
    mgmt_key_algorithm smallint,
    kek_version        smallint)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = blinkylite, pg_temp
AS $$
#variable_conflict use_column
DECLARE
    v_ids uuid[];
BEGIN
    PERFORM _bl_require_role(p_actor_roles, ARRAY['Admin', 'SecurityOfficer']);

    -- Lock the card's envelopes so that the list audited is the list returned.
    PERFORM 1 FROM card_secrets c WHERE c.card_serial = p_card_serial FOR UPDATE;

    SELECT array_agg(k.secret_id ORDER BY k.ord) INTO v_ids
    FROM _bl_mgmt_key_candidates(p_card_serial) k;

    PERFORM _bl_write_audit('mgmt-key.used', p_card_serial, NULL,
        jsonb_build_object('secret_ids', coalesce(to_jsonb(v_ids), '[]'::jsonb)),
        p_actor_upn, p_actor_sid, p_actor_roles, p_source_ip);

    RETURN QUERY
    SELECT k.secret_id, k.issuance_id, k.secret_state, k.envelope, k.mgmt_key_algorithm, k.kek_version
    FROM _bl_mgmt_key_candidates(p_card_serial) k
    ORDER BY k.ord;
END
$$;

-- Events that change no data - sign-in and refused sign-in. Deliberately a
-- short list: an open "write any action" function would let the server forge
-- a puk.disclosed or an issuance.issued that never happened.
CREATE FUNCTION bl_audit(
    p_action      text,
    p_data        jsonb,
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
    IF p_action IS NULL OR p_action NOT IN ('auth.login', 'auth.denied') THEN
        RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = format('bl_audit does not record %L', p_action);
    END IF;

    PERFORM _bl_write_audit(p_action, NULL, NULL, p_data,
        p_actor_upn, p_actor_sid, p_actor_roles, p_source_ip);
END
$$;
