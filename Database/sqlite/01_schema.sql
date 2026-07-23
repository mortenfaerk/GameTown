-- GameTown — SQLite schema
-- Translated from Database/postgres/01_schema.sql.
--
-- Identifiers stay double-quoted to preserve the original mixed casing
-- ("GameTownGame", "RAWGGames", "PasswordHash", ...). SQLite honours quoted
-- identifiers, so the scaffolded EFModel keeps emitting the same explicit
-- ToTable/HasColumnName calls and stays stable across re-scaffolds.
--
-- Type mapping from the Postgres original:
--   uuid              -> uniqueidentifier
--   varchar(n) / text -> TEXT     (SQLite does not enforce length — see below)
--   timestamptz       -> datetime (always UTC)
--   boolean           -> boolean
--   double precision  -> REAL
--   date              -> date
--
-- THE TYPE NAMES ABOVE ARE LOad-BEARING, even though SQLite itself barely cares
-- about them. SQLite is dynamically typed, so the EF scaffolder cannot infer a
-- CLR type from storage alone: given a bare `TEXT` column it emits `string`, and
-- given `INTEGER` it emits `int`. It recovers Guid/DateTime/bool only from the
-- *declared* type name. Writing `TEXT` here instead of `uniqueidentifier` silently
-- turns every `Guid Id` in EFModel into a `string Id` and every `bool` into an
-- `int`, which breaks the whole API. (The scaffolder will also guess Guid by
-- sniffing existing row values — do not rely on that: it makes the scaffold
-- non-deterministic and produces `string` against a fresh, empty install.)
--
-- Verified: these names scaffold to Guid/DateTime/bool against an EMPTY database,
-- and the resulting HasColumnType("uniqueidentifier") annotations do not disturb
-- the runtime mapping — EF still stores TEXT/INTEGER and Guid equality still
-- translates into a matching WHERE clause.
--
-- Three things differ from Postgres and are NOT visible in this file:
--
--   1. Foreign keys are OFF by default in SQLite. Every FOREIGN KEY below is
--      inert unless the connection sets `PRAGMA foreign_keys = ON`
--      ("Foreign Keys=True" in the connection string). Without it the
--      ON DELETE CASCADE on "RefreshTokens" silently stops cascading.
--
--   2. varchar(n) lengths are not enforced. The lengths are kept in comments
--      as documentation, but rejecting an over-long Username is now the
--      application's job, not the database's.
--
--   3. There is no gen_random_uuid(). Guid primary keys have no DEFAULT and are
--      generated client-side by EF — see EFModel/DatabaseContextConfiguration.cs,
--      which restores ValueGeneratedOnAdd because the scaffolder infers
--      ValueGeneratedNever for a key with no database default.
--
-- EF serialises a Guid to UPPERCASE 'D'-format text ('8F50B277-0B2D-...').
-- SQLite compares TEXT with binary collation, so any GUID literal written by hand
-- — seed data, fixtures, migration scripts — MUST be uppercase too. Lowercase
-- literals still *read* back fine (Guid.Parse is case-insensitive), which is what
-- makes this dangerous: the failure only shows up later, when an EF-inserted child
-- row's uppercase FK fails to match a hand-written lowercase parent key.

PRAGMA foreign_keys = ON;

BEGIN;

-- ---------------------------------------------------------------- GameTown core

CREATE TABLE "GameTownRoles" (
    "Id"           uniqueidentifier    NOT NULL PRIMARY KEY,            -- uuid
    "Role"         TEXT    NOT NULL,                        -- varchar(50)
    "CreatedBy"    TEXT    NOT NULL,                        -- varchar(50)
    "CreatedDate"  datetime    NOT NULL DEFAULT CURRENT_TIMESTAMP,
    "ModifiedBy"   TEXT    NOT NULL,                        -- varchar(50)
    "ModifiedDate" datetime    NOT NULL DEFAULT CURRENT_TIMESTAMP,
    "IsActive"     boolean  NOT NULL DEFAULT 1
);

CREATE TABLE "GameTownUsers" (
    "Id"             uniqueidentifier    NOT NULL PRIMARY KEY,          -- uuid
    "PasswordHash"   TEXT    NOT NULL,                      -- varchar(256)
    "Salt"           TEXT    NOT NULL,                      -- varchar(256)
    "Username"       TEXT    NOT NULL UNIQUE,               -- varchar(256)
    "DisplayName"    TEXT    NULL,                          -- varchar(256)
    "IsActive"       boolean  NOT NULL DEFAULT 1,
    "Notes"          TEXT    NULL,                          -- varchar(512)
    "CreatedAt"      datetime    NOT NULL DEFAULT CURRENT_TIMESTAMP,
    "CreatedBy"      TEXT    NULL,                          -- varchar(256)
    "LastModifiedAt" datetime    NULL DEFAULT CURRENT_TIMESTAMP,
    "LastModifiedBy" TEXT    NULL                           -- varchar(256)
);

CREATE TABLE "GameTownUsers_Roles" (
    "APIUserId" uniqueidentifier NOT NULL,
    "APIRoleId" uniqueidentifier NOT NULL,
    CONSTRAINT "PK_APIUsers_APIRoles" PRIMARY KEY ("APIUserId", "APIRoleId"),
    CONSTRAINT "FK_APIUsers_APIRoles_APIUserId" FOREIGN KEY ("APIUserId") REFERENCES "GameTownUsers" ("Id"),
    CONSTRAINT "FK_APIUsers_APIRoles_APIRoleId" FOREIGN KEY ("APIRoleId") REFERENCES "GameTownRoles" ("Id")
);

CREATE TABLE "RefreshTokens" (
    "Id"        uniqueidentifier    NOT NULL PRIMARY KEY,               -- uuid
    "Token"     TEXT    NOT NULL,                           -- varchar(512)
    "ExpiresAt" datetime    NOT NULL,
    "IsRevoked" boolean  NOT NULL DEFAULT 0,
    "CreatedAt" datetime    NOT NULL DEFAULT CURRENT_TIMESTAMP,
    "UserId"    uniqueidentifier    NOT NULL,
    CONSTRAINT "FK_RefreshTokens_Users" FOREIGN KEY ("UserId") REFERENCES "GameTownUsers" ("Id") ON DELETE CASCADE
);

-- ---------------------------------------------------------------- RAWG entities
-- RAWG rows reuse RAWG's own integer ids, so these keys are never generated.
-- NB: in SQLite, "INTEGER PRIMARY KEY" is an alias for rowid and would autoincrement.
-- That is harmless here because every insert supplies the id explicitly, and the
-- scaffolded model marks them ValueGeneratedNever.

CREATE TABLE "RAWGGames" (
    "id"                          INTEGER NOT NULL PRIMARY KEY,
    "slug"                        TEXT    NOT NULL,
    "name"                        TEXT    NOT NULL,
    "name_original"               TEXT    NULL,
    "description"                 TEXT    NOT NULL,
    "metacritic"                  INTEGER NULL,
    "released"                    date    NULL,
    "tba"                         boolean  NULL,
    "updated"                     datetime    NULL,
    "background_image"            TEXT    NULL,
    "background_image_additional" TEXT    NULL,
    "website"                     TEXT    NOT NULL,
    "rating"                      REAL    NULL,
    "rating_top"                  INTEGER NULL,
    "playtime"                    INTEGER NULL,
    "screenshots_count"           INTEGER NULL,
    "movies_count"                INTEGER NULL,
    "creators_count"              INTEGER NULL,
    "achievements_count"          INTEGER NULL,
    "parent_achievements_count"   INTEGER NULL,
    "reddit_url"                  TEXT    NULL,
    "reddit_count"                INTEGER NULL,
    "twitch_count"                INTEGER NULL,
    "youtube_count"               INTEGER NULL,
    "reviews_text_count"          INTEGER NULL,
    "ratings_count"               INTEGER NULL,
    "suggestions_count"           INTEGER NULL,
    "metacritic_url"              TEXT    NULL,
    "parents_count"               INTEGER NULL,
    "additions_count"             INTEGER NULL,
    "game_series_count"           INTEGER NULL,
    "reviews_count"               INTEGER NULL,
    "saturated_color"             TEXT    NULL,
    "dominant_color"              TEXT    NULL
);

CREATE TABLE "RAWGDevelopers" (
    "id"               INTEGER NOT NULL PRIMARY KEY,
    "name"             TEXT    NULL,
    "slug"             TEXT    NULL,
    "games_count"      INTEGER NULL,
    "image_background" TEXT    NULL
);

CREATE TABLE "RAWGGenres" (
    "id"               INTEGER NOT NULL PRIMARY KEY,
    "name"             TEXT    NULL,
    "slug"             TEXT    NULL,
    "image_background" TEXT    NULL
);

CREATE TABLE "RAWGScreenshots" (
    "Id"         INTEGER NOT NULL PRIMARY KEY,
    "image"      TEXT    NOT NULL,
    "width"      INTEGER NOT NULL,
    "height"     INTEGER NOT NULL,
    "is_deleted" boolean  NOT NULL DEFAULT 0
);

-- ---------------------------------------------------------------- join tables

CREATE TABLE "RAWGGames_Developers" (
    "game_id"      INTEGER NOT NULL,
    "developer_id" INTEGER NOT NULL,
    PRIMARY KEY ("game_id", "developer_id"),
    FOREIGN KEY ("game_id")      REFERENCES "RAWGGames" ("id"),
    FOREIGN KEY ("developer_id") REFERENCES "RAWGDevelopers" ("id")
);

CREATE TABLE "RAWGGames_Genres" (
    "game_id"  INTEGER NOT NULL,
    "genre_id" INTEGER NOT NULL,
    PRIMARY KEY ("game_id", "genre_id"),
    FOREIGN KEY ("game_id")  REFERENCES "RAWGGames" ("id"),
    FOREIGN KEY ("genre_id") REFERENCES "RAWGGenres" ("id")
);

CREATE TABLE "RAWGGames_Screenshots" (
    "gameid"       INTEGER NOT NULL,
    "screenshotid" INTEGER NOT NULL,
    PRIMARY KEY ("gameid", "screenshotid"),
    FOREIGN KEY ("gameid")       REFERENCES "RAWGGames" ("id"),
    FOREIGN KEY ("screenshotid") REFERENCES "RAWGScreenshots" ("Id")
);

-- ---------------------------------------------------------------- GameTown games
-- Declared last: FK depends on "RAWGGames".
-- Title is COLLATE NOCASE so the library's title search is case-insensitive
-- without depending on lower(). NB: NOCASE folds ASCII only — "Ø" still will not
-- match "ø". See SQLITE-APPLIANCE-PLAN.md.

CREATE TABLE "GameTownGame" (
    "Id"         uniqueidentifier    NOT NULL PRIMARY KEY,              -- uuid
    "Title"      TEXT    NOT NULL COLLATE NOCASE,           -- varchar(500)
    "HowTo"      TEXT    NOT NULL,
    "RAWGGameId" INTEGER NULL,
    "URL"        TEXT    NOT NULL,                          -- varchar(500)
    "Size"       REAL    NOT NULL,                          -- size in Mb
    CONSTRAINT "FK_GameTownGame_Games" FOREIGN KEY ("RAWGGameId") REFERENCES "RAWGGames" ("id")
);

COMMIT;
