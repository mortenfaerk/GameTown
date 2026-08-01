-- 005 — manual tags on GameTown games
--
-- RAWG genres describe what a game *is* ("Shooter", "Puzzle"). They cannot answer the question the
-- shelf actually gets asked on a Friday night: can four of us play this in the same room. That is a
-- property of how a group plays, not of the game's genre, and no external source knows it — so it is
-- entered by hand and stored here.
--
-- A table plus a join rather than a comma-separated column on "GameTownGame":
--   * filtering by tag becomes an indexed join instead of a LIKE over every row,
--   * "Co-op" spelled two ways cannot become two tags (see "Slug" below),
--   * and renaming a tag is one UPDATE rather than a rewrite of every game that carries it.
--
-- Everything here is IF NOT EXISTS / OR IGNORE, so unlike 003 and 004 this script IS safe to replay.
--
-- The ON DELETE CASCADE rules below are inert unless the connection enables foreign keys. It does —
-- API/Startup/SqliteConnectionString.cs forces "Foreign Keys=True" — but that is a runtime setting
-- rather than a property of the schema, so it is worth knowing that deleting a game relies on it to
-- clear the game's tag links.

CREATE TABLE IF NOT EXISTS "Tags" (
    "Id"         uniqueidentifier NOT NULL PRIMARY KEY,     -- uuid, generated client-side by EF
    "Name"       TEXT    NOT NULL COLLATE NOCASE,           -- varchar(50), as typed and as displayed
    -- The identity of a tag. Names are normalised to a slug (lowercased, runs of non-alphanumerics
    -- collapsed to '-') before insert, and UNIQUE here is what makes "LAN", "lan" and " Lan " one
    -- tag rather than three. NOCASE is deliberately NOT used: the slug is already lowercase by
    -- construction, so a NOCASE index would only cost.
    "Slug"       TEXT    NOT NULL UNIQUE,
    -- Marks the handful of tags offered as one-click buttons in the editor.
    --
    -- A column rather than a hardcoded list of four slugs in the UI: the buttons are then derived
    -- from the database, an admin can promote a fifth tag without a release, and the orphan cleanup
    -- in TagService has a principled reason to keep these rows when the last game drops them.
    "IsQuickAdd" boolean NOT NULL DEFAULT 0,
    -- Presentation order for the quick-add buttons. Ties (and every non-quick tag) fall back to name.
    "SortOrder"  INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS "GameTownGame_Tags" (
    "GameId" uniqueidentifier NOT NULL,
    "TagId"  uniqueidentifier NOT NULL,
    PRIMARY KEY ("GameId", "TagId"),
    FOREIGN KEY ("GameId") REFERENCES "GameTownGame" ("Id") ON DELETE CASCADE,
    FOREIGN KEY ("TagId")  REFERENCES "Tags" ("Id")         ON DELETE CASCADE
);

-- The join is queried in both directions: "the tags on this game" is served by the primary key, but
-- "the games carrying this tag" — which is what the library filter does — would otherwise scan.
CREATE INDEX IF NOT EXISTS "IX_GameTownGame_Tags_TagId"
    ON "GameTownGame_Tags" ("TagId");

-- The four quick tags, seeded rather than special-cased in code so they behave exactly like a
-- hand-typed tag everywhere else: they can be filtered on, counted, and applied through the same
-- path. Only "IsQuickAdd" sets them apart.
--
-- GUID literals are UPPERCASE. EF serialises a Guid to uppercase 'D'-format text and SQLite compares
-- TEXT with binary collation, so a lowercase literal here would still read back fine while failing to
-- match any FK an EF insert writes — the failure would surface later and somewhere else.
--
-- OR IGNORE, keyed off the UNIQUE slug, so a replay is a no-op and an install that already coined
-- "co-op" by hand keeps its own row (and simply gains the quick-add flag below).
INSERT OR IGNORE INTO "Tags" ("Id", "Name", "Slug", "IsQuickAdd", "SortOrder") VALUES
    ('A1D3F2B4-5C6E-4F70-8A91-0B2C3D4E5F60', 'Split screen', 'split-screen', 1, 1),
    ('B2E4A3C5-6D7F-4A81-9B02-1C3D4E5F6071', 'LAN',          'lan',          1, 2),
    ('C3F5B4D6-7E80-4B92-8C13-2D4E5F607182', 'Co-op',        'co-op',        1, 3),
    ('D4A6C5E7-8F91-4CA3-9D24-3E5F60718293', 'Competitive',  'competitive',  1, 4);

-- Covers the case the INSERT above deliberately skips: an install that already had a tag on one of
-- these slugs keeps its own row and its own id, and would otherwise never be offered as a button.
UPDATE "Tags" SET "IsQuickAdd" = 1
 WHERE "Slug" IN ('split-screen', 'lan', 'co-op', 'competitive');
