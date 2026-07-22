-- GameTown — seed data (roles + dev "test" user)
-- Translated from Database/PostDeploymentScripts/Script.PostDeployment.sql.
--
-- The GUIDs, PasswordHash and Salt below are copied VERBATIM from the SQL Server
-- seed. They must not be regenerated: the API recomputes SHA256 over the stored
-- salt at login, so any drift here breaks the seeded "test" user's credentials.
--
-- Safe to re-run: every insert is ON CONFLICT DO NOTHING.

BEGIN;

INSERT INTO "GameTownRoles" ("Id", "Role", "CreatedBy", "CreatedDate", "ModifiedBy", "ModifiedDate", "IsActive")
VALUES
    ('99ffbcba-6c26-416f-b996-33e8a0b4c6ef', 'Admin',       'System', now(), 'System', now(), true),
    ('37a3c94f-b2e0-46ac-a60b-2b9eb09c3a14', 'Contributor', 'System', now(), 'System', now(), true)
ON CONFLICT ("Id") DO NOTHING;

INSERT INTO "GameTownUsers" ("Id", "PasswordHash", "Salt", "Username", "DisplayName", "IsActive", "Notes", "CreatedAt", "CreatedBy", "LastModifiedAt", "LastModifiedBy")
VALUES (
    '8f50b277-0b2d-4245-b686-e9c77a32b966',
    'C59A7F1470254A8ABFD25CD44192EAA90A5CF41640B2530A31420A8399A83693',
    'A05611E9D30B3ED4D7A9A370A608ED98',
    'test',
    'Test User',
    true,
    'Default user for development environment',
    now(), 'System', now(), 'System'
)
ON CONFLICT ("Id") DO NOTHING;

INSERT INTO "GameTownUsers_Roles" ("APIUserId", "APIRoleId")
VALUES ('8f50b277-0b2d-4245-b686-e9c77a32b966', '99ffbcba-6c26-416f-b996-33e8a0b4c6ef')
ON CONFLICT ("APIUserId", "APIRoleId") DO NOTHING;

COMMIT;
