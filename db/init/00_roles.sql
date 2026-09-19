-- BlinkyLite: roles and database privileges.
--
-- Run ONCE, as a superuser (or a role with CREATEROLE that owns the database),
-- connected to the BlinkyLite database. The server itself never runs this:
-- CREATE ROLE needs a privilege the server does not have and should not have.
-- Safe to run again.
--
-- Passwords are not set here, so this file can live in git. Set them after:
--   ALTER ROLE blinkylite_owner    PASSWORD '...';
--   ALTER ROLE blinkylite_app      PASSWORD '...';
--   ALTER ROLE blinkylite_readonly PASSWORD '...';

DO $$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'blinkylite_owner') THEN
        CREATE ROLE blinkylite_owner LOGIN;
    END IF;
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'blinkylite_app') THEN
        CREATE ROLE blinkylite_app LOGIN;
    END IF;
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'blinkylite_readonly') THEN
        CREATE ROLE blinkylite_readonly LOGIN;
    END IF;

    -- The database belongs to BlinkyLite alone: nobody else connects, nobody
    -- else creates temporary tables that a function could be tricked into
    -- reading.
    EXECUTE format('REVOKE ALL ON DATABASE %I FROM PUBLIC', current_database());
    EXECUTE format('GRANT CONNECT, CREATE ON DATABASE %I TO blinkylite_owner', current_database());
    EXECUTE format('GRANT CONNECT ON DATABASE %I TO blinkylite_app, blinkylite_readonly', current_database());
END
$$;
