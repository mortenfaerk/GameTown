-- 008 — game suggestions pulled from the LAN Discord bot
--
-- The LAN this appliance is shown at runs a Discord bot where players suggest games to play. The bot
-- exposes a Catalogue API; this table is GameTown's local mirror of its /suggestions feed, plus the
-- link between a suggestion and the library entry it turned out to be.
--
-- THE BOT'S MODEL, because the shape of this table only makes sense against it: the bot's catalogue
-- is keyed by a GUID the *client* chooses, and it binds a suggestion to a catalogue entry whose title
-- equals the suggestion's name. GameTown therefore does not "read matches" — it CAUSES them, by
-- PUTting an entry under the game's own Id. And the bot holds a single match per catalogue entry, so
-- re-titling one replaces whatever was bound to it before.
--
-- Everything here is IF NOT EXISTS, so unlike 003, 004, 006 and 007 this script IS safe to replay.
--
-- A note on what this table is NOT: it is not a cache that can be rebuilt at will. "Dismissed" is a
-- human judgement ("nobody is ever going to upload this") that exists nowhere else, and FirstSeenUtc
-- outlives anything the bot reports. Deleting and re-pulling loses both.

CREATE TABLE IF NOT EXISTS "LanSuggestion" (
    "Id"            uniqueidentifier NOT NULL PRIMARY KEY,  -- uuid, generated client-side by EF
    -- The bot's own suggestion id. Declared bigint, not INTEGER: the bot reports it as an int64, and
    -- a bare INTEGER scaffolds to a C# int, which would truncate silently. The natural key we upsert
    -- on, since the bot is the only thing that coins these.
    "RemoteId"      bigint   NOT NULL,
    -- As a player typed it in Discord. Untrusted text — see SECURITY-NOTES.md risk 10. It is also the
    -- exact string pushed back as a catalogue title, because title equality is what makes the bot
    -- bind; normalising it before sending would break the binding it exists to create.
    "Name"          TEXT     NOT NULL COLLATE NOCASE,
    "LanEventName"  TEXT     NOT NULL COLLATE NOCASE,
    "Played"        boolean  NOT NULL DEFAULT 0,

    -- GameTown's OWN knowledge that this suggestion is this game. Deliberately many-to-one: several
    -- suggestions, across spellings and across LAN events, may point at one game. This is what drives
    -- the "suggested for" badge, the ?lan= shelf filter and the wishlist.
    --
    -- ON DELETE SET NULL rather than CASCADE: deleting a game does not mean the suggestion never
    -- happened. The row returns to the wishlist, which is the right prompt for someone to re-upload
    -- it. (Inert unless the connection enables foreign keys — API/Startup/SqliteConnectionString.cs
    -- forces "Foreign Keys=True", but that is a runtime setting, not a property of this schema.)
    "GameId"        uniqueidentifier NULL,

    -- 'auto'   — matched by SuggestionMatcher on an unambiguous normalised title
    -- 'manual' — a Contributor chose it on the LAN screen
    -- 'remote' — the bot already had the bind when we first saw it; we adopted it
    "LinkSource"    TEXT     NULL,

    -- What the bot CURRENTLY points at for this suggestion, as it reports it. A different claim from
    -- "GameId" above: GameId is what GameTown worked out, this is what the bot actually has. They come
    -- apart whenever a push has not happened yet or failed. NULL means the bot has no binding for this
    -- suggestion, whatever GameTown knows.
    "RemoteMatchId" uniqueidentifier NULL,

    -- Set only when GAMETOWN created the catalogue entry. This is what makes unlinking safe: the
    -- DELETE is only ever issued against an entry we recorded creating, never against one the crew
    -- made by hand inside the bot.
    "PushedAtUtc"   datetime NULL,

    -- "Nobody is going to upload this" — test entries, custom maps, things that are not games. Keeps
    -- the wishlist a list of work rather than a list of noise. A judgement, so it survives sync.
    "Dismissed"     boolean  NOT NULL DEFAULT 0,

    -- "A human has decided about this row; stop deciding for them."
    --
    -- Set by unlinking, cleared by linking. Without it, unlinking is not durable: auto-matching fires
    -- on exact normalised titles, so a suggestion whose link a Contributor had just removed would be
    -- matched to the very same game on the next tick and pushed again. The unlink would appear to
    -- work and quietly undo itself a few minutes later, which is the worst version of this bug —
    -- nothing fails, and the only symptom is a link that will not stay removed.
    --
    -- Deliberately not "which game was rejected". The useful statement is about the ROW, not the
    -- pairing: someone looked at this suggestion and dealt with it, so it belongs in their queue
    -- rather than in the matcher's.
    "AutoMatchBlocked" boolean NOT NULL DEFAULT 0,

    "FirstSeenUtc"  datetime NOT NULL,
    "LastSeenUtc"   datetime NOT NULL,

    CONSTRAINT "FK_LanSuggestion_Game" FOREIGN KEY ("GameId")
        REFERENCES "GameTownGame" ("Id") ON DELETE SET NULL
);

-- The natural key. Sync upserts on it, and UNIQUE is what stops a pull that overlaps a previous one
-- from duplicating the library's wishlist.
CREATE UNIQUE INDEX IF NOT EXISTS "IX_LanSuggestion_RemoteId"
    ON "LanSuggestion" ("RemoteId");

-- "Which suggestions point at this game" — the badge on every card on the shelf.
CREATE INDEX IF NOT EXISTS "IX_LanSuggestion_GameId"
    ON "LanSuggestion" ("GameId");

-- "Which games were suggested for this event" — the ?lan= filter.
CREATE INDEX IF NOT EXISTS "IX_LanSuggestion_LanEvent"
    ON "LanSuggestion" ("LanEventName" COLLATE NOCASE);

-- NOT UNIQUE, and the reason is worth recording because it is the opposite of what the bot's API
-- suggests.
--
-- A catalogue entry holds ONE TITLE, and re-PUTting it replaces that title. It is tempting to read
-- that as "one suggestion per game" — but the bindings are separate from the title and are ADDITIVE:
-- verified against the live bot, a PUT binds every suggestion whose name matches the new title and
-- unbinds nothing, including when the new title matches no suggestion at all. So several suggestions
-- routinely point at one matchId, and the only thing that changes is which of their names the
-- catalogue displays.
--
-- What the single title DOES constrain is pushing: GameTown pushes each suggestion at most once, so
-- two suggestions for one game each get their binding and the title then stays put. Pushing on every
-- sync instead would leave the catalogue title flip-flopping between two names forever.
CREATE INDEX IF NOT EXISTS "IX_LanSuggestion_Bound"
    ON "LanSuggestion" ("RemoteMatchId");
