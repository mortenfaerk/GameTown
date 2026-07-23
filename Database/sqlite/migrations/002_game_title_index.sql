-- 002 — index GameTownGame.Title
--
-- The library's search does `Title LIKE '%term%'` (GTGamesService), which cannot use an index for a
-- leading-wildcard match. This index is for the other two things that hit Title on every page of the
-- library: ordering, and the exact/prefix lookups. On a household-sized library it is a small win;
-- it exists mostly because it is a real, harmless schema change with which to exercise the migration
-- runner end to end.
--
-- Written with COLLATE NOCASE so the index matches how the column itself is declared — a
-- differently-collated index would simply be ignored by those comparisons.
--
-- Every migration must be safe to re-run: the runner records versions transactionally and will not
-- normally replay one, but an operator restoring a backup mid-upgrade should not be punished for it.

CREATE INDEX IF NOT EXISTS "IX_GameTownGame_Title" ON "GameTownGame" ("Title" COLLATE NOCASE);
