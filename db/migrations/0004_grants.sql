-- What the application and the read-only role may do. Deny by default: a
-- table, column or function added later is invisible to both until a new
-- migration grants it on purpose.

GRANT USAGE ON SCHEMA blinkylite TO blinkylite_app, blinkylite_readonly;

GRANT SELECT ON schema_migrations, cards, issuances, audit_events
    TO blinkylite_app, blinkylite_readonly;

-- Column list, not the table: puk_envelope and mgmt_key_envelope are missing
-- on purpose. With a plain table grant, a LINQ query could read every PUK in
-- the database without a single audit event.
GRANT SELECT (id, issuance_id, card_serial, state, mgmt_key_algorithm, kek_version,
              puk_disclosed_count, created_at, activated_at, retired_at)
    ON card_secrets
    TO blinkylite_app, blinkylite_readonly;

-- Belt and braces with the default privileges in 0001.
REVOKE EXECUTE ON ALL FUNCTIONS IN SCHEMA blinkylite FROM PUBLIC;

-- The API. The _bl_* helpers are not in this list and never will be.
GRANT EXECUTE ON FUNCTION
    bl_issuance_reserve,
    bl_issuance_customised,
    bl_issuance_attested,
    bl_issuance_submitted,
    bl_issuance_pending,
    bl_issuance_issued,
    bl_issuance_failed,
    bl_secret_disclose,
    bl_mgmt_key_candidates,
    bl_audit
TO blinkylite_app;
