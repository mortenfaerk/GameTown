-- 009 — cascade a user's role assignments when the user is deleted
--
-- Deleting a user who had any role assigned failed with a database error. "GameTownUsers_Roles"
-- (01_schema.sql) has never declared ON DELETE on either foreign key, which defaults to NO
-- ACTION — and EFModel/Models/DatabaseContext.cs scaffolded that as DeleteBehavior.ClientSetNull
-- on both sides of the join. ClientSetNull tries to null out the join row's key columns when a
-- parent is removed, but "APIUserId"/"APIRoleId" are the join's composite primary key and cannot
-- be null, so EF Core threw before SQLite was ever asked to reject anything.
--
-- A user's own role assignments have no meaning once the user is gone — the same reasoning
-- already applied to "GameTownGame_Tags" (005, ON DELETE CASCADE on both sides). Only the
-- "APIUserId" side gets that treatment here: deleting a ROLE still goes through the
-- application-level "in use" guard in UserService.DeleteRole, and this migration has no reason to
-- change that, so "APIRoleId" keeps its original implicit NO ACTION.
--
-- SQLite has no ALTER TABLE ... ADD CONSTRAINT, so the only way to change a FOREIGN KEY clause is
-- to rebuild the table: create the corrected version under a temporary name, copy every row
-- across, drop the original, and rename the copy into its place. NOT safe to replay, like
-- 003/004/006/007 — the temporary table would already exist on a second run.

CREATE TABLE "GameTownUsers_Roles_new" (
    "APIUserId" uniqueidentifier NOT NULL,
    "APIRoleId" uniqueidentifier NOT NULL,
    CONSTRAINT "PK_APIUsers_APIRoles" PRIMARY KEY ("APIUserId", "APIRoleId"),
    CONSTRAINT "FK_APIUsers_APIRoles_APIUserId" FOREIGN KEY ("APIUserId")
        REFERENCES "GameTownUsers" ("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_APIUsers_APIRoles_APIRoleId" FOREIGN KEY ("APIRoleId")
        REFERENCES "GameTownRoles" ("Id")
);

INSERT INTO "GameTownUsers_Roles_new" ("APIUserId", "APIRoleId")
    SELECT "APIUserId", "APIRoleId" FROM "GameTownUsers_Roles";

DROP TABLE "GameTownUsers_Roles";

ALTER TABLE "GameTownUsers_Roles_new" RENAME TO "GameTownUsers_Roles";
