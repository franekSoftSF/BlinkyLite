-- bl_issuance_reserve takes the issuance id from the server.
--
-- An envelope's AAD binds it to its issuance id (docs/03-data-model.md), and
-- the server has to encrypt the PUK and the management key BEFORE it calls
-- reserve (D-03). With the id generated inside the function the server could
-- not know it in time. 0002 is applied and immutable, so the old signature is
-- dropped here and the function recreated with p_id first; the body is
-- otherwise unchanged.

DROP FUNCTION bl_issuance_reserve(
    bigint, text, boolean, text, text, text, text, text, text, text, text, text,
    bytea, bytea, smallint, smallint, text, text, text[], inet);

-- Reserved: the server has generated the PUK and the management key and
-- stores them BEFORE the workstation touches the card (D-03).
CREATE FUNCTION bl_issuance_reserve(
    p_id                  uuid,
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
    v_id         uuid := p_id;
    v_open       uuid;
    v_constraint text;
BEGIN
    PERFORM _bl_require_role(p_actor_roles, ARRAY['Admin', 'SecurityOfficer']);

    IF p_id IS NULL THEN
        RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'the issuance id is required';
    END IF;

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

REVOKE EXECUTE ON FUNCTION bl_issuance_reserve FROM PUBLIC;
GRANT EXECUTE ON FUNCTION bl_issuance_reserve TO blinkylite_app;
