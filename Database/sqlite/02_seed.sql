-- GameTown — seed data (roles only)
-- Translated from Database/postgres/02_seed.sql.
--
-- GUIDs are written UPPERCASE, hyphenated ('D' format) because that is exactly
-- how EF Core's SQLite provider serialises a Guid to TEXT, and SQLite compares
-- TEXT with binary collation. Lowercase literals would still read back correctly
-- (Guid.Parse ignores case) but would not MATCH an EF-written value — so the
-- rows below would not MATCH values EF writes later. Keep these uppercase.
--
-- Safe to re-run: every insert is ON CONFLICT DO NOTHING.


PRAGMA foreign_keys = ON;

BEGIN;

INSERT INTO "GameTownRoles" ("Id", "Role", "CreatedBy", "CreatedDate", "ModifiedBy", "ModifiedDate", "IsActive")
VALUES
    ('99FFBCBA-6C26-416F-B996-33E8A0B4C6EF', 'Admin',       'System', CURRENT_TIMESTAMP, 'System', CURRENT_TIMESTAMP, 1),
    ('37A3C94F-B2E0-46AC-A60B-2B9EB09C3A14', 'Contributor', 'System', CURRENT_TIMESTAMP, 'System', CURRENT_TIMESTAMP, 1)
ON CONFLICT ("Id") DO NOTHING;

-- No user is seeded. The first administrator is created by the first-run wizard at /setup,
-- which stops responding as soon as an admin exists.
--
-- There used to be a "test" / "123456" Admin here with its hash in the repo. That was defensible for
-- a single dev box and indefensible for something other people install: it would be a known default
-- credential on every deployment, and the kind nobody changes.

COMMIT;
