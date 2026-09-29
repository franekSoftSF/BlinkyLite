-- Wycofanie klucza z użycia (0058).
--
-- Klucz skasowany poza BlinkyLite - zresetowany, zgubiony, oddany - zostawia
-- w bazie zapis, który kłamie: "znam PUK tej karty". To jest gorsze niż brak
-- zapisu. Helpdesk odczyta PUK, który niczego nie otwiera, a zdalne
-- odblokowanie (0057) wyda go stacji i spali próby na karcie.
--
-- Dlatego nie ma tu żadnego DELETE. Wydanie przechodzi w stan `Withdrawn`,
-- koperta w `Retired`, a karta traci bieżące wydanie. Obie drogi do sekretów -
-- bl_secret_disclose i bl_unlock_request - patrzą na kopertę `Active`, więc
-- po tej jednej zmianie przestają cokolwiek wydawać, bez dotykania ich kodu.
--
-- Czego to NIE robi: nie kasuje niczego na karcie (BlinkyLite pisze na kartę
-- tylko przy wydaniu i przy nowym PIN-ie) i nie zamyka drogi do ponownego
-- wydania - wyczyszczona karta jest fabryczna i wolno ją wydać na nowo.

ALTER TABLE issuances DROP CONSTRAINT issuances_state_check;
ALTER TABLE issuances ADD CONSTRAINT issuances_state_check CHECK (state IN
    ('Reserved', 'Customised', 'Attested', 'PendingCa', 'Issued', 'Failed', 'Superseded', 'Withdrawn'));

-- Wycofać można tylko wydanie, które doszło do certyfikatu, więc niezmiennik
-- "ma certyfikat" obowiązuje dalej.
ALTER TABLE issuances DROP CONSTRAINT issuances_certificate_when_issued;
ALTER TABLE issuances ADD CONSTRAINT issuances_certificate_when_issued CHECK (
    state NOT IN ('Issued', 'Superseded', 'Withdrawn')
    OR (certificate_der IS NOT NULL AND cert_thumbprint IS NOT NULL
        AND cert_not_before IS NOT NULL AND cert_not_after IS NOT NULL));

-- Kto, kiedy i po co - obok wydania, a nie tylko w audycie, bo to pytanie
-- pada przy tym jednym wierszu, a nie przy przeglądaniu dziennika. Trafia
-- też do eksportu do Blinky (D-19, docs/10).
ALTER TABLE issuances ADD COLUMN withdrawn_at     timestamptz NULL;
ALTER TABLE issuances ADD COLUMN withdrawn_by     text        NULL;
ALTER TABLE issuances ADD COLUMN withdrawn_reason text        NULL;

CREATE FUNCTION bl_card_withdraw(
    p_serial      bigint,
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
    c            cards;
    v_issuance   uuid;
    v_issuances  integer := 0;
    v_secrets    integer := 0;
BEGIN
    -- Ci sami, co wydają: kto może klucz wydać, ten może go wycofać. Żadne
    -- nowe uprawnienie tu nie powstaje.
    PERFORM _bl_require_role(p_actor_roles, ARRAY['Admin', 'SecurityOfficer']);

    IF p_reason IS NULL OR length(btrim(p_reason)) < 5 THEN
        RAISE EXCEPTION USING
            ERRCODE = 'BL005',
            MESSAGE = 'error.reason.required',
            DETAIL  = 'wycofanie wymaga powodu, co najmniej 5 znakow';
    END IF;

    SELECT * INTO c FROM cards WHERE serial = p_serial FOR UPDATE;
    IF NOT FOUND THEN
        RAISE EXCEPTION USING
            ERRCODE = 'BL002',
            MESSAGE = 'error.not-found',
            DETAIL  = format('karta %s', p_serial);
    END IF;

    v_issuance := c.current_issuance_id;

    IF v_issuance IS NOT NULL THEN
        UPDATE issuances
        SET state = 'Withdrawn',
            withdrawn_at = clock_timestamp(),
            withdrawn_by = p_actor_upn,
            withdrawn_reason = btrim(p_reason),
            updated_at = now()
        WHERE id = v_issuance AND state = 'Issued';
        GET DIAGNOSTICS v_issuances = ROW_COUNT;
    END IF;

    -- Koperta nie znika - może być jedyną kopią management key karty, która
    -- leży na czyimś biurku. Przestaje tylko być tą, którą wolno odsłonić.
    UPDATE card_secrets
    SET state = 'Retired', retired_at = clock_timestamp()
    WHERE card_serial = p_serial AND state = 'Active';
    GET DIAGNOSTICS v_secrets = ROW_COUNT;

    IF v_issuances = 0 AND v_secrets = 0 THEN
        RAISE EXCEPTION USING
            ERRCODE = 'BL001',
            MESSAGE = 'error.card.withdrawn',
            DETAIL  = format('karta %s nie ma czego wycofac', p_serial);
    END IF;

    UPDATE cards SET current_issuance_id = NULL WHERE serial = p_serial;

    -- Zgłoszenie odblokowania w toku dotyczy PUK-a, którego już nie ma po co
    -- wydawać.
    UPDATE unlock_requests SET state = 'Expired'
    WHERE card_serial = p_serial AND state IN ('Pending', 'Approved');

    PERFORM _bl_write_audit('card.withdrawn', p_serial, v_issuance,
        jsonb_build_object('reason', btrim(p_reason), 'issuance', v_issuance,
                           'secrets_retired', v_secrets),
        p_actor_upn, p_actor_sid, p_actor_roles, p_source_ip);
END
$$;

REVOKE EXECUTE ON FUNCTION bl_card_withdraw FROM PUBLIC;
GRANT EXECUTE ON FUNCTION bl_card_withdraw TO blinkylite_app;
