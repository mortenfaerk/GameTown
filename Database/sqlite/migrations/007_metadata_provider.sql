-- 007 — provider-neutral game metadata, and the move off RAWG
--
-- RAWG is gone. What replaces it is IGDB, but the lesson of losing a metadata source is not "pick a
-- better one" — it is that the schema should not be shaped like whichever one we happen to be using.
-- The tables below carry a "provider" column and the provider's own id, so a second source is a new
-- value in a column rather than a second set of tables.
--
-- ---------------------------------------------------------------- why this can run offline
--
-- Every image RAWG ever supplied is ALREADY on this machine. RAWGService.RehostImageAsync has always
-- downloaded covers and screenshots into the data directory and stored a "/media/{guid}.ext" path,
-- never a CDN URL. So an installed library is a complete, self-sufficient copy of its own metadata,
-- and this migration is a pure local data move: no network, nothing to re-fetch, and no way for it to
-- fail because a provider is down. That is the whole reason the upgrade needs nothing from the
-- operator and nothing from install.sh.
--
-- ---------------------------------------------------------------- why the ids are seeded from RAWG's
--
-- The primary key here is a surrogate, because two providers can collide on an integer id. But the
-- copy below seeds it from RAWG's own id, which collapses the riskiest step of the whole migration —
-- re-pointing every GameTownGame at its metadata — into an UPDATE with no join and no matching logic:
--
--     UPDATE "GameTownGame" SET "MetadataId" = "RAWGGameId";
--
-- New IGDB rows autoincrement from above the highest copied id, because "INTEGER PRIMARY KEY" is an
-- alias for the rowid. The provider's own id lives in "external_id" and is never a key.
--
-- ---------------------------------------------------------------- why nothing is dropped
--
-- This script is purely additive. The RAWG tables and "GameTownGame"."RAWGGameId" stay exactly where
-- they are, for three separate reasons:
--
--   * "RAWGGameId" CANNOT be dropped. It is named in the table-level FK_GameTownGame_Games
--     constraint, and SQLite refuses ALTER TABLE ... DROP COLUMN on a column a constraint references.
--     Removing it means the full 12-step table-rebuild dance, and PRAGMA foreign_keys is a no-op
--     inside the transaction SchemaMigrator wraps this script in.
--   * Dropping "RAWGGames" on its own is worse than useless: with foreign keys on — and
--     API/Startup/SqliteConnectionString.cs forces them on — every later GameTownGame write fails
--     against a missing FK target.
--   * It keeps the release reversible. Restoring the previous build over an already-migrated database
--     still works, because the old columns still hold the old truth.
--
-- Declared column type names are load-bearing, as everywhere else in this schema: the scaffolder
-- reads the declared name to pick a CLR type ("date" -> DateTime?, "boolean" -> bool). A bare TEXT
-- would silently scaffold to string.
--
-- ON RE-RUNNING: every statement except the ALTER is replayable (IF NOT EXISTS / OR IGNORE, and the
-- INSERT ... SELECTs are keyed on the destination's primary key). The ALTER is not — SQLite has no
-- "ADD COLUMN IF NOT EXISTS" — which is the same documented exception 003, 004 and 006 carry.

-- ---------------------------------------------------------------- neutral metadata entities

CREATE TABLE IF NOT EXISTS "MetadataGames" (
    -- Surrogate. Seeded from RAWG ids by the copy below; assigned by SQLite for everything after.
    "id"           INTEGER NOT NULL PRIMARY KEY,
    -- 'rawg' for rows carried across by this migration, 'igdb' for anything fetched since. A library
    -- that never runs the re-link tool stays on 'rawg' forever, and that is a supported state, not a
    -- pending task — the rows are complete and their images are local.
    "provider"     TEXT    NOT NULL,
    -- The provider's own id for this game. Not a key: it is only unique *within* a provider.
    "external_id"  INTEGER NOT NULL,
    "slug"         TEXT    NOT NULL,
    "name"         TEXT    NOT NULL,
    -- HTML, sanitised on the way out by GameMappings. RAWG served HTML; IGDB serves plain text and is
    -- HTML-encoded at ingest so this column stays one thing rather than two.
    "description"  TEXT    NOT NULL,
    "released"     date    NULL,
    -- RAWG's metacritic and IGDB's aggregated_rating, both 0-100 critic aggregates. REAL rather than
    -- INTEGER because IGDB's is fractional (91.538…); the UI rounds it.
    "critic_score" REAL    NULL,
    -- The provider's user rating, on whatever scale that provider uses. Displayed by nothing today.
    "rating"       REAL    NULL,
    -- The game's main image, as a local "/media/{guid}.ext" path. Never a remote URL: every image is
    -- downloaded onto this server first, through ImageFetcher, whatever its source.
    "image"        TEXT    NULL,
    "website"      TEXT    NOT NULL DEFAULT '',
    "updated"      datetime NULL,
    UNIQUE ("provider", "external_id")
);

CREATE TABLE IF NOT EXISTS "MetadataDevelopers" (
    "id"          INTEGER NOT NULL PRIMARY KEY,
    "provider"    TEXT    NOT NULL,
    "external_id" INTEGER NOT NULL,
    "name"        TEXT    NULL,
    "slug"        TEXT    NULL,
    UNIQUE ("provider", "external_id")
);

CREATE TABLE IF NOT EXISTS "MetadataGenres" (
    "id"          INTEGER NOT NULL PRIMARY KEY,
    "provider"    TEXT    NOT NULL,
    "external_id" INTEGER NOT NULL,
    "name"        TEXT    NULL,
    "slug"        TEXT    NULL,
    UNIQUE ("provider", "external_id")
);

CREATE TABLE IF NOT EXISTS "MetadataScreenshots" (
    "id"          INTEGER NOT NULL PRIMARY KEY,
    "provider"    TEXT    NOT NULL,
    "external_id" INTEGER NOT NULL,
    -- Local "/media/{guid}.ext", on the same terms as MetadataGames.image.
    "image"       TEXT    NOT NULL,
    "width"       INTEGER NOT NULL,
    "height"      INTEGER NOT NULL,
    "is_deleted"  boolean NOT NULL DEFAULT 0,
    UNIQUE ("provider", "external_id")
);

-- ---------------------------------------------------------------- join tables

CREATE TABLE IF NOT EXISTS "MetadataGames_Developers" (
    "metadata_id"  INTEGER NOT NULL,
    "developer_id" INTEGER NOT NULL,
    PRIMARY KEY ("metadata_id", "developer_id"),
    FOREIGN KEY ("metadata_id")  REFERENCES "MetadataGames" ("id")      ON DELETE CASCADE,
    FOREIGN KEY ("developer_id") REFERENCES "MetadataDevelopers" ("id") ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS "MetadataGames_Genres" (
    "metadata_id" INTEGER NOT NULL,
    "genre_id"    INTEGER NOT NULL,
    PRIMARY KEY ("metadata_id", "genre_id"),
    FOREIGN KEY ("metadata_id") REFERENCES "MetadataGames" ("id")  ON DELETE CASCADE,
    FOREIGN KEY ("genre_id")    REFERENCES "MetadataGenres" ("id") ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS "MetadataGames_Screenshots" (
    "metadata_id"   INTEGER NOT NULL,
    "screenshot_id" INTEGER NOT NULL,
    PRIMARY KEY ("metadata_id", "screenshot_id"),
    FOREIGN KEY ("metadata_id")   REFERENCES "MetadataGames" ("id")       ON DELETE CASCADE,
    FOREIGN KEY ("screenshot_id") REFERENCES "MetadataScreenshots" ("id") ON DELETE CASCADE
);

-- ---------------------------------------------------------------- carry the library across
--
-- A no-op on a fresh install: the RAWG tables exist (the baseline creates them) and are empty, so
-- every SELECT below returns nothing. That is what lets a fresh install and an upgraded one run the
-- identical sequence, which is the property that stops the two drifting.
--
-- The RAWG-shaped columns nothing renders — reddit_count, twitch_count, suggestions_count,
-- dominant_color, playtime and the rest — are deliberately NOT carried across. Grep the Blazor pages:
-- none of them reach a screen. They stay in "RAWGGames" for anyone who wants them.
--
-- "background_image" becomes "image" and is already a local /media path on any real install.
-- "metacritic" becomes "critic_score". COALESCE on website/description because those columns are
-- NOT NULL here and rows written before the snake_case deserialisation fix can hold NULL.

INSERT OR IGNORE INTO "MetadataGames"
    ("id", "provider", "external_id", "slug", "name", "description",
     "released", "critic_score", "rating", "image", "website", "updated")
SELECT "id", 'rawg', "id",
       COALESCE("slug", ''), COALESCE("name", ''), COALESCE("description", ''),
       "released", "metacritic", "rating", "background_image", COALESCE("website", ''), "updated"
  FROM "RAWGGames";

INSERT OR IGNORE INTO "MetadataDevelopers" ("id", "provider", "external_id", "name", "slug")
SELECT "id", 'rawg', "id", "name", "slug" FROM "RAWGDevelopers";

INSERT OR IGNORE INTO "MetadataGenres" ("id", "provider", "external_id", "name", "slug")
SELECT "id", 'rawg', "id", "name", "slug" FROM "RAWGGenres";

INSERT OR IGNORE INTO "MetadataScreenshots"
    ("id", "provider", "external_id", "image", "width", "height", "is_deleted")
SELECT "Id", 'rawg', "Id", "image", "width", "height", "is_deleted" FROM "RAWGScreenshots";

INSERT OR IGNORE INTO "MetadataGames_Developers" ("metadata_id", "developer_id")
SELECT "game_id", "developer_id" FROM "RAWGGames_Developers";

INSERT OR IGNORE INTO "MetadataGames_Genres" ("metadata_id", "genre_id")
SELECT "game_id", "genre_id" FROM "RAWGGames_Genres";

INSERT OR IGNORE INTO "MetadataGames_Screenshots" ("metadata_id", "screenshot_id")
SELECT "gameid", "screenshotid" FROM "RAWGGames_Screenshots";

-- ---------------------------------------------------------------- re-point the library
--
-- A REFERENCES clause on ADD COLUMN is allowed only when the default is NULL, which it is. The FK is
-- what makes the scaffolder generate the navigation property.
ALTER TABLE "GameTownGame" ADD COLUMN "MetadataId" INTEGER NULL REFERENCES "MetadataGames" ("id");

-- The payoff of seeding the surrogate from RAWG's id: no join, no matching, no way to attach a game
-- to someone else's metadata.
UPDATE "GameTownGame" SET "MetadataId" = "RAWGGameId" WHERE "RAWGGameId" IS NOT NULL;

-- "the games carrying this metadata row" is asked on every delete (to decide whether the shared
-- record and its images are still in use) and by the re-link tool.
CREATE INDEX IF NOT EXISTS "IX_GameTownGame_MetadataId" ON "GameTownGame" ("MetadataId");

-- Provider is the re-link tool's working set: "everything still on the retired source".
CREATE INDEX IF NOT EXISTS "IX_MetadataGames_Provider" ON "MetadataGames" ("provider");

-- ---------------------------------------------------------------- retire the dead credential
--
-- The key authenticates against a service that is not answering. Leaving a dead secret in the
-- database serves nobody, and "no row means the coded default" already holds in SettingsService.
DELETE FROM "Settings" WHERE "Key" = 'RAWGApiKey';
