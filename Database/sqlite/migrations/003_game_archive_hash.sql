-- 003 — record each archive's SHA-256, so the same file cannot be added to the library twice
--
-- The motivating case is a retry after an apparent failure. A long upload that outlives a reverse
-- proxy's read timeout reports a network error to the browser while the server goes on to store the
-- archive and write the row. The contributor sees a failure, uploads the same file again, and ends
-- up with two library entries and two copies on disk — the second of which nothing will ever clean
-- up, because as far as the database is concerned it is a legitimate second game.
--
-- Content hash rather than an idempotency token: it identifies the upload by what it actually is, so
-- it also catches "somebody already added this" between two different contributors, and it needs
-- nothing to be remembered by the client between attempts.
--
-- NULLable, and existing rows are left NULL rather than backfilled. Hashing an existing library
-- means reading every archive off disk — potentially hundreds of gigabytes over a CIFS mount — at
-- the one moment an upgrade must not stall. Games uploaded before this migration therefore do not
-- participate in the check; games uploaded after it do, which is the case the guard exists for.
--
-- NOTE ON RE-RUNNING: unlike 002, the ALTER below is NOT safe to replay — SQLite has no
-- "ADD COLUMN IF NOT EXISTS" and no conditional DDL, so a second run fails with "duplicate column
-- name". That is a deliberate exception to the rule in CLAUDE.md rather than an oversight, because
-- the rule cannot be honoured for this statement. The runner is version-gated and commits each
-- script with its version row, so a replay requires an operator to restore a database whose
-- SchemaVersion disagrees with its own schema.
ALTER TABLE "GameTownGame" ADD COLUMN "ArchiveSha256" TEXT NULL;

-- The column exists to be looked up by, once per upload, and never to be ordered or ranged over.
-- Declared BINARY (the default) rather than NOCASE like Title: this is a hex digest written by one
-- code path, so case never varies, and a NOCASE index here would only cost.
CREATE INDEX IF NOT EXISTS "IX_GameTownGame_ArchiveSha256"
    ON "GameTownGame" ("ArchiveSha256");
