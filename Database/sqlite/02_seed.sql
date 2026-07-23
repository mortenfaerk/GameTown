-- GameTown — seed data (roles + dev "test" user)
-- Translated from Database/postgres/02_seed.sql.
--
-- The GUIDs, PasswordHash and Salt below are copied VERBATIM from the Postgres
-- seed. They must not be regenerated: the API recomputes SHA256 over the stored
-- salt at login, so any drift here breaks the seeded "test" user's credentials.
--
-- GUIDs are written UPPERCASE, hyphenated ('D' format) because that is exactly
-- how EF Core's SQLite provider serialises a Guid to TEXT, and SQLite compares
-- TEXT with binary collation. Lowercase literals would still read back correctly
-- (Guid.Parse ignores case) but would not MATCH an EF-written value — so the
-- "GameTownUsers_Roles" join below, and any RefreshToken EF later inserts for
-- this user, would silently fail to line up. Keep these uppercase.
--
-- Safe to re-run: every insert is ON CONFLICT DO NOTHING.
--
-- NB: the "test" / "123456" account is a DEVELOPMENT convenience and is removed
-- in Phase 4 in favour of first-run admin creation. Do not ship it in an
-- installable build — a known default credential on every install is a very
-- different risk from a known credential on one personal LAN box.

PRAGMA foreign_keys = ON;

BEGIN;

INSERT INTO "GameTownRoles" ("Id", "Role", "CreatedBy", "CreatedDate", "ModifiedBy", "ModifiedDate", "IsActive")
VALUES
    ('99FFBCBA-6C26-416F-B996-33E8A0B4C6EF', 'Admin',       'System', CURRENT_TIMESTAMP, 'System', CURRENT_TIMESTAMP, 1),
    ('37A3C94F-B2E0-46AC-A60B-2B9EB09C3A14', 'Contributor', 'System', CURRENT_TIMESTAMP, 'System', CURRENT_TIMESTAMP, 1)
ON CONFLICT ("Id") DO NOTHING;

INSERT INTO "GameTownUsers" ("Id", "PasswordHash", "Salt", "Username", "DisplayName", "IsActive", "Notes", "CreatedAt", "CreatedBy", "LastModifiedAt", "LastModifiedBy")
VALUES (
    '8F50B277-0B2D-4245-B686-E9C77A32B966',
    'C59A7F1470254A8ABFD25CD44192EAA90A5CF41640B2530A31420A8399A83693',
    'A05611E9D30B3ED4D7A9A370A608ED98',
    'test',
    'Test User',
    1,
    'Default user for development environment',
    CURRENT_TIMESTAMP, 'System', CURRENT_TIMESTAMP, 'System'
)
ON CONFLICT ("Id") DO NOTHING;

INSERT INTO "GameTownUsers_Roles" ("APIUserId", "APIRoleId")
VALUES ('8F50B277-0B2D-4245-B686-E9C77A32B966', '99FFBCBA-6C26-416F-B996-33E8A0B4C6EF')
ON CONFLICT ("APIUserId", "APIRoleId") DO NOTHING;

COMMIT;
