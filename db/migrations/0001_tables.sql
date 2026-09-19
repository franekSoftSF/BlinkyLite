-- Tables. Owned by blinkylite_owner; the application gets SELECT only
-- (0004_grants.sql), and every write goes through a bl_* function.
-- The schema and schema_migrations are created by the migrator itself.

-- PostgreSQL grants EXECUTE on every new function to PUBLIC. Per-schema
-- default privileges can only add to the global ones, never take away, so
-- this has to be the global form - without it a function added by a later
-- migration is callable by every role in the cluster until 0004 runs again.
ALTER DEFAULT PRIVILEGES FOR ROLE blinkylite_owner REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;

CREATE TABLE cards (
    serial              bigint      PRIMARY KEY CHECK (serial > 0),
    firmware            text        NOT NULL CHECK (firmware <> ''),
    form_factor         smallint    NULL,
    has_puk             boolean     NOT NULL,
    first_seen_at       timestamptz NOT NULL DEFAULT now(),
    current_issuance_id uuid        NULL
);

CREATE TABLE issuances (
    id                           uuid        PRIMARY KEY,
    card_serial                  bigint      NOT NULL REFERENCES cards (serial),
    state                        text        NOT NULL CHECK (state IN
        ('Reserved', 'Customised', 'Attested', 'PendingCa', 'Issued', 'Failed', 'Superseded')),

    -- Exactly one backslash, and none of the characters that would break the
    -- "requestername=DOMAIN\user" attribute the CA parses (Blinky refused
    -- them rather than escaping, and so do we).
    target_sam                   text        NOT NULL
        CHECK (target_sam ~ '^[^\\]+\\[^\\]+$' AND target_sam !~ '[&=[:cntrl:]]'),
    target_upn                   text        NOT NULL CHECK (target_upn <> ''),
    target_sid                   text        NOT NULL CHECK (target_sid ~ '^S-1-[0-9]+(-[0-9]+)+$'),
    target_display_name          text        NOT NULL,

    -- Copied from the profile at reservation time, so that changing
    -- appsettings.json later does not rewrite history.
    profile_name                 text        NOT NULL CHECK (profile_name <> ''),
    template_name                text        NOT NULL CHECK (template_name <> ''),
    ca_config                    text        NOT NULL CHECK (ca_config <> ''),
    ca_request_id                integer     NULL CHECK (ca_request_id > 0),

    operator_upn                 text        NOT NULL,
    operator_sid                 text        NOT NULL,
    windows_identity             text        NOT NULL CHECK (windows_identity <> ''),
    ea_thumbprint                text        NULL,
    workstation                  text        NOT NULL CHECK (workstation <> ''),

    key_algorithm                text        NULL,
    pin_policy                   smallint    NULL,
    touch_policy                 smallint    NULL,
    attestation_der              bytea       NULL,
    attestation_intermediate_der bytea       NULL,
    csr_der                      bytea       NULL,

    certificate_der              bytea       NULL,
    cert_serial                  text        NULL,
    cert_thumbprint              text        NULL,
    cert_not_before              timestamptz NULL,
    cert_not_after               timestamptz NULL,

    error                        text        NULL,
    created_at                   timestamptz NOT NULL DEFAULT now(),
    updated_at                   timestamptz NOT NULL DEFAULT now(),
    completed_at                 timestamptz NULL,

    CONSTRAINT issuances_certificate_when_issued CHECK (
        state NOT IN ('Issued', 'Superseded')
        OR (certificate_der IS NOT NULL AND cert_thumbprint IS NOT NULL
            AND cert_not_before IS NOT NULL AND cert_not_after IS NOT NULL)),
    CONSTRAINT issuances_error_when_failed CHECK (
        state <> 'Failed' OR (error IS NOT NULL AND error <> ''))
);

-- One issuance in flight per card. bl_issuance_reserve checks this under the
-- card's row lock; the index is what makes it true even if that check is ever
-- wrong.
CREATE UNIQUE INDEX issuances_one_open_per_card ON issuances (card_serial)
    WHERE state IN ('Reserved', 'Customised', 'Attested', 'PendingCa');
CREATE INDEX issuances_card_serial ON issuances (card_serial);
CREATE INDEX issuances_target_sid ON issuances (target_sid);
CREATE INDEX issuances_target_sam ON issuances (lower(target_sam));
CREATE INDEX issuances_created_at ON issuances (created_at);

ALTER TABLE cards
    ADD CONSTRAINT cards_current_issuance FOREIGN KEY (current_issuance_id) REFERENCES issuances (id);

CREATE TABLE card_secrets (
    id                  uuid        PRIMARY KEY,
    issuance_id         uuid        NOT NULL UNIQUE REFERENCES issuances (id),
    card_serial         bigint      NOT NULL REFERENCES cards (serial),
    state               text        NOT NULL CHECK (state IN ('Reserved', 'Active', 'Retired')),
    -- The two envelopes are the only columns the application role cannot
    -- SELECT (0004): the one way to them is bl_secret_disclose, which writes
    -- the audit event in the same transaction.
    puk_envelope        bytea       NULL,
    mgmt_key_envelope   bytea       NOT NULL,
    -- 0x03 3DES, 0x08 AES-128, 0x0A AES-192, 0x0C AES-256 (GET METADATA 9B).
    mgmt_key_algorithm  smallint    NOT NULL CHECK (mgmt_key_algorithm IN (3, 8, 10, 12)),
    kek_version         smallint    NOT NULL CHECK (kek_version > 0),
    puk_disclosed_count integer     NOT NULL DEFAULT 0 CHECK (puk_disclosed_count >= 0),
    created_at          timestamptz NOT NULL DEFAULT now(),
    activated_at        timestamptz NULL,
    retired_at          timestamptz NULL
);

-- A card has one management key at a time, so at most one Active envelope.
CREATE UNIQUE INDEX card_secrets_one_active_per_card ON card_secrets (card_serial)
    WHERE state = 'Active';
CREATE INDEX card_secrets_card_serial ON card_secrets (card_serial);

CREATE TABLE audit_events (
    id          bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    at          timestamptz NOT NULL DEFAULT clock_timestamp(),
    actor_upn   text        NOT NULL CHECK (actor_upn <> ''),
    -- Unknown only when the login itself failed: a wrong password yields no
    -- objectSid to record.
    actor_sid   text        NULL CHECK (actor_sid <> ''),
    actor_roles text[]      NOT NULL DEFAULT '{}',
    action      text        NOT NULL CHECK (action <> ''),
    card_serial bigint      NULL REFERENCES cards (serial),
    issuance_id uuid        NULL REFERENCES issuances (id),
    data        jsonb       NOT NULL DEFAULT '{}',
    source_ip   inet        NULL,

    CONSTRAINT audit_events_sid_known CHECK (actor_sid IS NOT NULL OR action = 'auth.denied')
);

CREATE INDEX audit_events_at ON audit_events (at);
CREATE INDEX audit_events_card_serial ON audit_events (card_serial);
CREATE INDEX audit_events_issuance_id ON audit_events (issuance_id);
CREATE INDEX audit_events_action ON audit_events (action);
