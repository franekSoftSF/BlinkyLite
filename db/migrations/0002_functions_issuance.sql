-- Issuance write path. Every public function here:
--   * is SECURITY DEFINER with a fixed search_path - without the fixed path a
--     caller could put its own object in front of ours and have it run with
--     the owner's rights;
--   * locks the row it changes, checks the state it starts from, changes the
--     data and writes the audit event, all in the caller's single statement.
--     If the audit insert fails, the change is rolled back with it.
-- Errors carry our own SQLSTATE class BL and a message KEY, not a sentence;
-- the client translates the key (docs/07-database.md, docs/08).

-- Internal helpers. Not SECURITY DEFINER and never granted: they run with the
-- rights of the bl_* function that calls them.

CREATE FUNCTION _bl_write_audit(
    p_action      text,
    p_card_serial bigint,
    p_issuance_id uuid,
    p_data        jsonb,
    p_actor_upn   text,
    p_actor_sid   text,
    p_actor_roles text[],
    p_source_ip   inet)
RETURNS void
LANGUAGE sql
SET search_path = blinkylite, pg_temp
AS $$
    INSERT INTO audit_events (actor_upn, actor_sid, actor_roles, action, card_serial, issuance_id, data, source_ip)
    VALUES (p_actor_upn, p_actor_sid, coalesce(p_actor_roles, '{}'), p_action, p_card_serial, p_issuance_id,
            coalesce(p_data, '{}'), p_source_ip);
$$;

-- The server checks roles through ASP.NET Core policies; this is the second
-- lock on the doors that matter, so that a bug in a policy is not enough.
CREATE FUNCTION _bl_require_role(p_actor_roles text[], p_allowed text[])
RETURNS void
LANGUAGE plpgsql
SET search_path = blinkylite, pg_temp
AS $$
BEGIN
    IF p_actor_roles IS NULL OR NOT (p_actor_roles && p_allowed) THEN
        RAISE EXCEPTION USING
            ERRCODE = 'BL004',
            MESSAGE = 'error.forbidden',
            DETAIL  = format('roles %s, required one of %s', coalesce(p_actor_roles::text, '{}'), p_allowed);
    END IF;
END
$$;

CREATE FUNCTION _bl_lock_issuance(p_id uuid, p_allowed text[])
RETURNS issuances
LANGUAGE plpgsql
SET search_path = blinkylite, pg_temp
AS $$
DECLARE
    r issuances;
BEGIN
    SELECT * INTO r FROM issuances WHERE id = p_id FOR UPDATE;
    IF NOT FOUND THEN
        RAISE EXCEPTION USING
            ERRCODE = 'BL002',
            MESSAGE = 'error.not-found',
            DETAIL  = format('issuance %s', p_id);
    END IF;
    IF NOT (r.state = ANY (p_allowed)) THEN
        RAISE EXCEPTION USING
            ERRCODE = 'BL001',
            MESSAGE = 'error.issuance.invalid-state',
            DETAIL  = format('issuance %s is %s, expected one of %s', p_id, r.state, p_allowed);
    END IF;
    RETURN r;
END
$$;

-- Reserved: the server has generated the PUK and the management key and
-- stores them BEFORE the workstation touches the card (D-03).
CREATE FUNCTION bl_issuance_reserve(
    p_card_serial         bigint,
    p_firmware            text,
    p_has_puk             boolean,
    p_target_sam          text,
    p_target_upn          text,
    p_target_sid          text,
    p_target_display_name text,
    p_profile_name        text,
    p_template_name       text,
    p_ca_config           text,
    p_windows_identity    text,
    p_workstation         text,
    p_puk_envelope        bytea,
    p_mgmt_key_envelope   bytea,
    p_mgmt_key_algorithm  smallint,
    p_kek_version         smallint,
    p_actor_upn           text,
    p_actor_sid           text,
    p_actor_roles         text[],
    p_source_ip           inet)
RETURNS uuid
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = blinkylite, pg_temp
AS $$
DECLARE
    v_id         uuid := gen_random_uuid();
    v_open       uuid;
    v_constraint text;
BEGIN
    PERFORM _bl_require_role(p_actor_roles, ARRAY['Admin', 'SecurityOfficer']);

    IF p_has_puk IS DISTINCT FROM (p_puk_envelope IS NOT NULL) THEN
        RAISE EXCEPTION USING
            ERRCODE = '22023',
            MESSAGE = 'a PUK envelope is required exactly when the card has a PUK';
    END IF;

    -- The upsert takes the card's row lock. Two reservations for the same
    -- card queue here, and the second one's next statement sees the first
    -- one's issuance.
    INSERT INTO cards (serial, firmware, has_puk)
    VALUES (p_card_serial, p_firmware, p_has_puk)
    ON CONFLICT (serial) DO UPDATE SET firmware = EXCLUDED.firmware, has_puk = EXCLUDED.has_puk;

    SELECT id INTO v_open
    FROM issuances
    WHERE card_serial = p_card_serial AND state IN ('Reserved', 'Customised', 'Attested', 'PendingCa')
    LIMIT 1;
    IF FOUND THEN
        RAISE EXCEPTION USING
            ERRCODE = 'BL003',
            MESSAGE = 'error.card.reserved-elsewhere',
            DETAIL  = format('card %s has open issuance %s', p_card_serial, v_open);
    END IF;

    INSERT INTO issuances (
        id, card_serial, state,
        target_sam, target_upn, target_sid, target_display_name,
        profile_name, template_name, ca_config,
        operator_upn, operator_sid, windows_identity, workstation)
    VALUES (
        v_id, p_card_serial, 'Reserved',
        p_target_sam, p_target_upn, p_target_sid, p_target_display_name,
        p_profile_name, p_template_name, p_ca_config,
        p_actor_upn, p_actor_sid, p_windows_identity, p_workstation);

    INSERT INTO card_secrets (
        id, issuance_id, card_serial, state,
        puk_envelope, mgmt_key_envelope, mgmt_key_algorithm, kek_version)
    VALUES (
        gen_random_uuid(), v_id, p_card_serial, 'Reserved',
        p_puk_envelope, p_mgmt_key_envelope, p_mgmt_key_algorithm, p_kek_version);

    PERFORM _bl_write_audit('issuance.reserved', p_card_serial, v_id,
        jsonb_build_object(
            'target_sam', p_target_sam,
            'target_sid', p_target_sid,
            'profile', p_profile_name,
            'firmware', p_firmware,
            'workstation', p_workstation,
            'windows_identity', p_windows_identity),
        p_actor_upn, p_actor_sid, p_actor_roles, p_source_ip);

    RETURN v_id;
EXCEPTION
    WHEN unique_violation THEN
        GET STACKED DIAGNOSTICS v_constraint = CONSTRAINT_NAME;
        IF v_constraint = 'issuances_one_open_per_card' THEN
            RAISE EXCEPTION USING
                ERRCODE = 'BL003',
                MESSAGE = 'error.card.reserved-elsewhere',
                DETAIL  = format('card %s has an open issuance', p_card_serial);
        END IF;
        RAISE;
END
$$;

-- Customised: the card now holds this reservation's management key, PUK and
-- the user's PIN. From here on this envelope is the card's only copy of its
-- management key, so it becomes Active and the previous one is retired.
CREATE FUNCTION bl_issuance_customised(
    p_id          uuid,
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
    r issuances;
BEGIN
    PERFORM _bl_require_role(p_actor_roles, ARRAY['Admin', 'SecurityOfficer']);
    r := _bl_lock_issuance(p_id, ARRAY['Reserved']);

    -- Retire first: the partial unique index allows one Active per card.
    UPDATE card_secrets SET state = 'Retired', retired_at = now()
    WHERE card_serial = r.card_serial AND state = 'Active';

    UPDATE card_secrets SET state = 'Active', activated_at = now()
    WHERE issuance_id = p_id AND state = 'Reserved';
    IF NOT FOUND THEN
        RAISE EXCEPTION USING
            ERRCODE = 'BL001',
            MESSAGE = 'error.issuance.invalid-state',
            DETAIL  = format('issuance %s has no reserved secrets', p_id);
    END IF;

    UPDATE issuances SET state = 'Customised', updated_at = now() WHERE id = p_id;

    PERFORM _bl_write_audit('issuance.customised', r.card_serial, p_id, '{}',
        p_actor_upn, p_actor_sid, p_actor_roles, p_source_ip);
END
$$;

-- Attested: key generated on the card, attestation verified on the
-- workstation and again on the server. Allowed from Attested too: resuming
-- an interrupted issuance regenerates the key and must record the new proof.
CREATE FUNCTION bl_issuance_attested(
    p_id                  uuid,
    p_attestation_der     bytea,
    p_intermediate_der    bytea,
    p_csr_der             bytea,
    p_key_algorithm       text,
    p_pin_policy          smallint,
    p_touch_policy        smallint,
    p_form_factor         smallint,
    p_actor_upn           text,
    p_actor_sid           text,
    p_actor_roles         text[],
    p_source_ip           inet)
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = blinkylite, pg_temp
AS $$
DECLARE
    r issuances;
BEGIN
    PERFORM _bl_require_role(p_actor_roles, ARRAY['Admin', 'SecurityOfficer']);
    r := _bl_lock_issuance(p_id, ARRAY['Customised', 'Attested']);

    IF p_attestation_der IS NULL OR p_intermediate_der IS NULL OR p_csr_der IS NULL
       OR p_key_algorithm IS NULL OR p_key_algorithm = '' THEN
        RAISE EXCEPTION USING
            ERRCODE = '22023',
            MESSAGE = 'attestation, intermediate, CSR and key algorithm are required';
    END IF;

    UPDATE issuances SET
        state = 'Attested',
        attestation_der = p_attestation_der,
        attestation_intermediate_der = p_intermediate_der,
        csr_der = p_csr_der,
        key_algorithm = p_key_algorithm,
        pin_policy = p_pin_policy,
        touch_policy = p_touch_policy,
        updated_at = now()
    WHERE id = p_id;

    UPDATE cards SET form_factor = p_form_factor WHERE serial = r.card_serial;

    PERFORM _bl_write_audit('issuance.attested', r.card_serial, p_id,
        jsonb_build_object(
            'key_algorithm', p_key_algorithm,
            'pin_policy', p_pin_policy,
            'touch_policy', p_touch_policy,
            'form_factor', p_form_factor),
        p_actor_upn, p_actor_sid, p_actor_roles, p_source_ip);
END
$$;

-- Submitted: recorded BEFORE waiting for the CA's answer. Without the request
-- id, a certificate the CA issued but the workstation never wrote to the card
-- cannot be found again.
CREATE FUNCTION bl_issuance_submitted(
    p_id            uuid,
    p_ca_request_id integer,
    p_ea_thumbprint text,
    p_actor_upn     text,
    p_actor_sid     text,
    p_actor_roles   text[],
    p_source_ip     inet)
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = blinkylite, pg_temp
AS $$
DECLARE
    r issuances;
BEGIN
    PERFORM _bl_require_role(p_actor_roles, ARRAY['Admin', 'SecurityOfficer']);
    r := _bl_lock_issuance(p_id, ARRAY['Attested']);

    IF p_ea_thumbprint IS NULL OR p_ea_thumbprint = '' THEN
        RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'the enrolment agent thumbprint is required';
    END IF;

    UPDATE issuances SET
        ca_request_id = p_ca_request_id,
        ea_thumbprint = p_ea_thumbprint,
        updated_at = now()
    WHERE id = p_id;

    -- The previous request id, if any, survives here: a resumed issuance
    -- submits again and overwrites the column.
    PERFORM _bl_write_audit('issuance.submitted', r.card_serial, p_id,
        jsonb_build_object(
            'ca_request_id', p_ca_request_id,
            'previous_ca_request_id', r.ca_request_id,
            'ea_thumbprint', p_ea_thumbprint),
        p_actor_upn, p_actor_sid, p_actor_roles, p_source_ip);
END
$$;

-- PendingCa: the CA answered "under submission" and a certificate manager
-- has to approve it; resumed later with RetrievePending(ca_request_id).
CREATE FUNCTION bl_issuance_pending(
    p_id          uuid,
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
    r issuances;
BEGIN
    PERFORM _bl_require_role(p_actor_roles, ARRAY['Admin', 'SecurityOfficer']);
    r := _bl_lock_issuance(p_id, ARRAY['Attested']);

    IF r.ca_request_id IS NULL THEN
        RAISE EXCEPTION USING
            ERRCODE = 'BL001',
            MESSAGE = 'error.issuance.invalid-state',
            DETAIL  = format('issuance %s was never submitted to the CA', p_id);
    END IF;

    UPDATE issuances SET state = 'PendingCa', updated_at = now() WHERE id = p_id;

    PERFORM _bl_write_audit('issuance.pending', r.card_serial, p_id,
        jsonb_build_object('ca_request_id', r.ca_request_id),
        p_actor_upn, p_actor_sid, p_actor_roles, p_source_ip);
END
$$;

-- Issued: the certificate is on the card and has been read back. The card's
-- previous Issued issuance, if any, is superseded in the same transaction.
CREATE FUNCTION bl_issuance_issued(
    p_id              uuid,
    p_certificate_der bytea,
    p_cert_serial     text,
    p_cert_thumbprint text,
    p_not_before      timestamptz,
    p_not_after       timestamptz,
    p_actor_upn       text,
    p_actor_sid       text,
    p_actor_roles     text[],
    p_source_ip       inet)
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = blinkylite, pg_temp
AS $$
DECLARE
    r        issuances;
    v_old_id uuid;
BEGIN
    PERFORM _bl_require_role(p_actor_roles, ARRAY['Admin', 'SecurityOfficer']);
    r := _bl_lock_issuance(p_id, ARRAY['Attested', 'PendingCa']);

    IF p_certificate_der IS NULL OR p_cert_thumbprint IS NULL OR p_cert_thumbprint = ''
       OR p_not_before IS NULL OR p_not_after IS NULL OR p_not_after <= p_not_before THEN
        RAISE EXCEPTION USING
            ERRCODE = '22023',
            MESSAGE = 'certificate, thumbprint and a valid validity period are required';
    END IF;

    FOR v_old_id IN
        SELECT id FROM issuances
        WHERE card_serial = r.card_serial AND state = 'Issued' AND id <> p_id
        FOR UPDATE
    LOOP
        UPDATE issuances SET state = 'Superseded', updated_at = now() WHERE id = v_old_id;
        PERFORM _bl_write_audit('issuance.superseded', r.card_serial, v_old_id,
            jsonb_build_object('superseded_by', p_id),
            p_actor_upn, p_actor_sid, p_actor_roles, p_source_ip);
    END LOOP;

    UPDATE issuances SET
        state = 'Issued',
        certificate_der = p_certificate_der,
        cert_serial = p_cert_serial,
        cert_thumbprint = p_cert_thumbprint,
        cert_not_before = p_not_before,
        cert_not_after = p_not_after,
        updated_at = now(),
        completed_at = now()
    WHERE id = p_id;

    UPDATE cards SET current_issuance_id = p_id WHERE serial = r.card_serial;

    PERFORM _bl_write_audit('issuance.issued', r.card_serial, p_id,
        jsonb_build_object(
            'cert_serial', p_cert_serial,
            'cert_thumbprint', p_cert_thumbprint,
            'not_after', p_not_after),
        p_actor_upn, p_actor_sid, p_actor_roles, p_source_ip);
END
$$;

-- Failed: any issuance still in flight. The envelopes are NOT touched - a
-- reservation that never finished may be the only copy of the management key
-- of a card lying on somebody's desk.
CREATE FUNCTION bl_issuance_failed(
    p_id          uuid,
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
    r issuances;
BEGIN
    PERFORM _bl_require_role(p_actor_roles, ARRAY['Admin', 'SecurityOfficer']);
    r := _bl_lock_issuance(p_id, ARRAY['Reserved', 'Customised', 'Attested', 'PendingCa']);

    IF p_error IS NULL OR btrim(p_error) = '' THEN
        RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'a failed issuance needs the reason it failed';
    END IF;

    UPDATE issuances SET
        state = 'Failed',
        error = p_error,
        updated_at = now(),
        completed_at = now()
    WHERE id = p_id;

    PERFORM _bl_write_audit('issuance.failed', r.card_serial, p_id,
        jsonb_build_object('from_state', r.state, 'error', p_error),
        p_actor_upn, p_actor_sid, p_actor_roles, p_source_ip);
END
$$;
