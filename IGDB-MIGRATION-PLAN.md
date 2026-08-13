# Plan: RAWG → IGDB metadata provider

RAWG is going away (or has already, depending on which hour you ask it). Everything that made it the
metadata source has to move to [IGDB](https://www.igdb.com), which is Twitch-owned, actively
maintained, and has an authentication story that is not "a query-string key".

---

## Status: implemented

All five phases are done on branch `igdb-migration`. The plan below is kept as the record of what was
decided and why; where the built thing differs from what was planned, the difference is noted inline.

| Phase | State |
|---|---|
| 1 — schema `007`, re-scaffold, switch reads | **Done** |
| 2 — IGDB provider | **Done**, verified against the live API |
| 3 — UI and first-run | **Done** |
| 4 — admin re-link tool | **Done** |
| 5 — tests and docs | **Done** — 203 tests (was 185) |

Three things were learned by building it that the plan had guessed at:

1. **The scaffolder marked all four new surrogate keys `ValueGeneratedNever()`** — exactly the trap
   predicted, arrived at from the opposite direction to the Guid keys. Restored in
   `DatabaseContextConfiguration.cs`, which lives outside `Models/` so a re-scaffold cannot delete it.
2. **`where game_type = 0` is needed for search to be usable at all.** Without it, searching
   "half-life" returns three Half-Life 2 mods before Half-Life itself.
3. **Apicalypse injection is real, and silent.** IGDB does not reject a query broken out of its
   search string — it answers it. Confirmed against the live API.

The IGDB field details the plan flagged as unverified were all checked with live calls before the
mapping was written, and all held: epoch `first_release_date`, plain-text `summary`, fractional
`aggregated_rating`, `involved_companies[].developer`, and the `t_cover_big` / `t_720p` image
templates.

---

**The constraint that shapes everything below: there are installs out there.** The one-liner
(`curl … | bash`) is the upgrade path, and it must carry an existing library across this change
without the operator doing anything, without losing a single game, and — critically — without needing
RAWG to still be answering when it runs.

That last point is what makes this tractable, and it is worth stating before anything else:

> **Every image RAWG ever gave this application is already on the operator's own disk.**
> `RAWGService.RehostImageAsync` has always downloaded covers and screenshots into `/media` rather
> than hot-linking. So has box art. The database holds `/media/{guid}.ext` paths, not CDN URLs.

An existing library therefore contains a complete, self-sufficient copy of its own metadata. The
migration does not have to re-fetch anything, which means it does not have to reach RAWG, which means
it cannot fail because RAWG is down. It is a pure local data move.

---

## The two layers

This is the centre of the plan. The migration splits along a line that decides almost every other
question:

| | Layer 1 — **carry the library across** | Layer 2 — **re-link to IGDB** |
|---|---|---|
| When | Automatically, at first start after upgrade | Whenever an admin chooses, per game or in bulk |
| Network | None | IGDB |
| Mechanism | Migration `007`, embedded in the binary | Admin screen + background job |
| If never run | — (always runs) | Library keeps rendering off frozen local data, forever |
| Risk | Deterministic SQL, testable offline | Wrong match; recoverable by re-matching |

Layer 1 is the answer to "the one-liner must migrate people's existing installs". Layer 2 is a
feature, not an upgrade step, and no install is required to run it.

### What the installer has to change: nothing

`install.sh` needs **zero changes**, and this is by design rather than luck — CLAUDE.md already
records why. Migration `007` ships as an embedded resource in `API.dll`; `SchemaMigrator` applies it
at startup before the first request is served. Taking the new build *is* taking the new schema. The
installer's existing responsibilities already cover this release:

- stop the service (nothing holds the database open),
- back up `gametown.db` and its `-wal`/`-shm` sidecars,
- swap `/opt/gametown`,
- start it again.

No new data subdirectory is needed either — IGDB art lands in the same `$DATA_DIR/media` that RAWG
art already uses. `.github/workflows/install-test.yml` should be extended to roll a real install back
to schema version 6 and re-run the installer over it, which is the existing pattern.

---

## On RAWG's status

For the record: I could not find a formal retirement announcement — what is documented publicly is
years of instability, a full-day outage, and the service being described as unmaintained, with other
projects ([GameVault](https://gamevau.lt/blog/2024/05/07/) among them) moving to IGDB for exactly
that reason. `api.rawg.io` shows as down at the time of writing.

This plan does not depend on resolving it. It never calls RAWG, and it works identically whether RAWG
is dead or merely unreliable. The one place it matters is Decision 4 below (whether to keep a RAWG
client at all), and the answer there is the same either way.

---

## What I need from you

Not an API key — **a Twitch developer application**. IGDB authenticates through Twitch's OAuth:

1. A Twitch account with 2FA enabled (required to register an app).
2. Register an application at <https://dev.twitch.tv/console/apps> — OAuth redirect URL can be
   `http://localhost`, it is unused by the client-credentials flow.
3. That yields a **Client ID** and a **Client Secret**. Both are needed; they are the pair that
   replaces the single `RAWGApiKey`.

Dev credentials are sufficient for the whole of this work. Rate limit is 4 requests/second with at
most 8 in flight, which the re-link tool has to respect (see traps).

---

## Decisions

### 1. Provider-neutral tables, not `IGDBGames`

The RAWG tables are RAWG-shaped down to the column names (`reddit_count`, `twitch_count`,
`saturated_color`). Recreating that mistake with IGDB-shaped tables guarantees a third round of this
exercise when IGDB is next acquired. The new tables carry a provider column instead:

Built as `MetadataGames` / `MetadataDevelopers` / `MetadataGenres` / `MetadataScreenshots` rather than
the `GameMetadata*` names sketched here — EF's scaffolder singularises a table name into an entity
name, and `GameMetadata` becomes `GameMetadatum`. The shape below is otherwise exactly what shipped,
except that `critic_score` is `REAL` rather than `INTEGER`: IGDB's `aggregated_rating` is fractional
(91.538…), which the plan did not know until the first live call.

```sql
CREATE TABLE "MetadataGames" (
    "id"           INTEGER NOT NULL PRIMARY KEY,   -- surrogate; see below
    "provider"     TEXT    NOT NULL,               -- 'rawg' | 'igdb'
    "external_id"  INTEGER NOT NULL,               -- the provider's own id
    "slug"         TEXT    NOT NULL,
    "name"         TEXT    NOT NULL,
    "description"  TEXT    NOT NULL,               -- HTML, sanitised on the way out
    "released"     date    NULL,
    "critic_score" REAL    NULL,                   -- RAWG metacritic / IGDB aggregated_rating
    "rating"       REAL    NULL,
    "image"        TEXT    NULL,                   -- '/media/{guid}.ext'
    "website"      TEXT    NOT NULL DEFAULT '',
    "updated"      datetime NULL,
    UNIQUE ("provider", "external_id")
);
```

…plus `MetadataDevelopers`, `MetadataGenres`, `MetadataScreenshots` and their join tables, following
the same shape. Everything RAWG-specific that nothing renders (`reddit_url`, `twitch_count`,
`suggestions_count`, `dominant_color` — grep the Blazor pages; none are used) is **not** carried
forward. It stays in the old tables if anyone ever wants it.

Keep the declared type names (`date`, `datetime`, `boolean`, `uniqueidentifier`) — CLAUDE.md's rule
about the scaffolder reading declared names, not storage classes, applies to every new column here.

### 2. INTEGER surrogate PK, seeded from RAWG's ids

The PK is a surrogate rather than the provider's id, because two providers can collide on an integer.
But it is seeded from RAWG's ids during the copy, and that one choice collapses the riskiest part of
the migration into a single statement:

```sql
UPDATE "GameTownGame" SET "MetadataId" = "RAWGGameId" WHERE "RAWGGameId" IS NOT NULL;
```

No join, no matching, no way to mis-associate a game with someone else's metadata. New IGDB rows
autoincrement from above the highest copied id, because `INTEGER PRIMARY KEY` is a rowid alias.

(Guid PKs would have matched the GameTown-entity convention, but generating v4 GUIDs in pure SQLite
means `randomblob`/`hex` gymnastics and the uppercase-`D`-format trap, for no benefit. These are
provider rows, and provider rows already use integers here.)

### 3. Migration 007 is purely additive — it drops nothing

Tempting to clean up. Do not, for three separate reasons:

- **`GameTownGame.RAWGGameId` cannot be dropped anyway.** It is named in the table-level
  `FK_GameTownGame_Games` constraint, and SQLite refuses `DROP COLUMN` on a column referenced by a
  constraint. Removing it means the full 12-step table-rebuild dance, and `PRAGMA foreign_keys`
  cannot be toggled inside the transaction `SchemaMigrator` wraps each script in.
- **Dropping `RAWGGames` alone is worse than useless** — with foreign keys on (and
  `SqliteConnectionString` forces them on), every subsequent `GameTownGame` insert fails against a
  missing FK target.
- **It keeps the release reversible.** If the upgrade goes wrong, restoring the previous build over
  the *migrated* database still works: the old columns still hold the old truth. That is a cheaper
  safety net than the installer's backup, and it costs nothing but some dead tables.

So `007` is: `CREATE TABLE` × 6, `INSERT … SELECT` × 6, `ALTER TABLE … ADD COLUMN` × 1, `UPDATE` × 1,
`DELETE` × 1. On a fresh install the `INSERT … SELECT`s copy zero rows and the migration is a no-op —
which is exactly the property that lets fresh and upgraded installs run the identical sequence.

Replayability: everything except the `ADD COLUMN` is safe to re-run. The `ADD COLUMN` is not, for the
reason `003`/`004`/`006` already document. Follow their comment convention.

Also in `007`: `DELETE FROM "Settings" WHERE "Key" = 'RAWGApiKey'`. A dead credential should not sit
in the database, and "missing row means the coded default" already holds in `SettingsService`.

### 4. RAWG's client is deleted, not kept as a second provider

`RAWGService` goes. Keeping it behind the new interface would mean maintaining a client for a service
that cannot be authenticated against, doubling the ingest surface for zero users. The *data* survives
(Decision 3), which is the part that matters. If RAWG returns, re-adding a provider behind
`IMetadataProvider` is a small, self-contained piece of work.

### 5. One provider interface, mirroring `IBoxArtProvider`

```csharp
public interface IGameMetadataProvider
{
    string Id { get; }                                   // 'igdb'
    Task<bool> IsConfiguredAsync();
    Task<IReadOnlyList<MetadataSearchResult>> SearchAsync(string query, int page, int pageSize);
    Task<ProviderGame?> GetAsync(long externalId);
}
```

The repo already has this pattern for box art and the reasoning is identical — the choice of source is
genuinely open. `IgdbProvider` implements it; `GameMetadataService` owns persistence (the successor to
`EnsureRawgGamePersisted`, with the same resolve-before-attach discipline against duplicate keys).

### 6. Contracts get renamed outright; no wire-compat shim

The API serves the SPA from its own `wwwroot`, so client and server ship as one artifact and cannot
be at different versions. `RawgGameContract` → `GameMetadataContract`, `GameContract.RawgGame` →
`.Metadata`, `AddGameRequest.RawgGameId` → `.ProviderGameId`, `/meta/getGame/{id}` keeps its path. This
does break any third-party script hitting `/meta` — accepted; those endpoints are
`RequireAuthorization("Contributor")` and exist to serve the picker.

`ProviderGameId`, not `MetadataId`, and the distinction is load-bearing: at add time no
`GameMetadata` row exists yet. The picker hands the browser IGDB's **external** id, and
`GameMetadataService` resolves it to a surrogate PK while persisting. A field named `MetadataId` on
the wire would invite exactly the wrong read — treating the provider's id as the local FK, which is
the collision Decision 2 introduced the surrogate to avoid.

---

## Phases

Each phase builds green and is verifiable against a running app. **Phases 1–3 must ship as one
release** — between 1 and 2 there is no working metadata search. Phase 4 can follow later.

### Phase 1 — schema, copy-forward, and switch the reads

*No network, no new dependency, no UI change.*

- `Database/sqlite/migrations/007_metadata_provider.sql` (Decisions 1–3).
- Re-scaffold `EFModel` per CLAUDE.md, delete the generated `OnConfiguring`.
- `GameMappings`, `GTGamesService` includes, `Contracts` → the new entities.
- Metadata *search/fetch* is stubbed to "no provider configured" in this phase.
- Delete `RAWGService`, `API/Models/Games/RAWGData/*`.

**How you will know it works:** boot against a *populated* v6 database and every game renders exactly
as before — same cover, same screenshots, same genres, same developers, same description. Extend
`SchemaTests`' populated-fixture test (it currently pins `MAX(Version) = 6` → `7`) to assert the
copied rows field by field, and that `MetadataId` points at the row `RAWGGameId` used to.

### Phase 2 — the IGDB provider

- `IgdbTokenProvider` (singleton): Twitch client-credentials, cached token, refresh on expiry and on
  401, `SemaphoreSlim` against a token-endpoint stampede.
- `IgdbProvider`: `POST https://api.igdb.com/v4/games`, Apicalypse body, `Client-ID` +
  `Authorization: Bearer` headers.
- Settings keys `IGDBClientId` / `IGDBClientSecret`; both optional, exactly as `RAWGApiKey` was —
  without them the app runs and metadata is entered by hand.
- `POST /settings/test-igdb-credentials` replaces `/test-rawg-key`.
- Image ingest continues through `ImageFetcher` → `MediaStore`. **No bypass**, even though the host is
  now a fixed `images.igdb.com`.

One dotted-expander query replaces RAWG's screenshot pagination loop entirely:

```
fields name, slug, summary, first_release_date, aggregated_rating, rating, url,
       cover.image_id, screenshots.image_id, screenshots.width, screenshots.height,
       genres.name, genres.slug, involved_companies.company.name,
       involved_companies.company.slug, involved_companies.developer;
where id = 1234;
```

*Field-level details here are from documentation I could not fetch directly (api-docs.igdb.com 403s
to automated clients). Verify with one live call as soon as credentials exist, before the mapping is
written.*

**How you will know it works:** search a game, attach it, and the cover and screenshots appear under
`/media` on disk. `SettingsTests`' pattern applies directly — boot with no credentials, save them,
assert the running provider sees them without a restart.

### Phase 3 — UI and first-run

- `RawgGamePicker`/`RawgGameCard` → `MetadataPicker`/`MetadataCard`; "Browse RAWG" nav item; the
  RAWG-worded copy in `AddGame`, `GameDetail`, `Settings`, `Home`.
- `Settings.razor`: the RAWG card becomes an IGDB card with two fields.
- `Setup.cshtml(.cs)`: the optional RAWG key field becomes optional Client ID + Secret.
- **An upgrade banner on the settings page**, shown when the library has `provider = 'rawg'` rows and
  no IGDB credentials: *"Metadata now comes from IGDB. Your existing library is unaffected. Add
  credentials to search for new games."* Without this, an operator upgrades and finds the picker
  silently dead with no explanation.

### Phase 4 — the re-link tool *(ships later; optional to run)*

**Settled: nothing here ever runs on its own.** No startup hook, no background sweep, no "heal the
library on first boot after upgrade". It is a screen in the admin interface that an administrator
opens and drives, and a library that never opens it is in a supported, indefinite state — see
Decision 7.

An admin screen listing games whose metadata is still `provider = 'rawg'`:

- Per game, IGDB candidates by name (+ release year where known), best match highlighted.
- Bulk "match all exact title matches" — an action the admin presses, presented as a reviewable list
  of proposed pairings rather than applied on click. Everything inexact goes to the same list,
  unticked.
- Resumable and throttled to ≤4 requests/second, so closing the tab mid-run loses nothing.
- **Never touches curated data**: `BoxArtUrl`, `Tags`, `HowTo`, `GuideBaked` are the contributor's
  work, not the provider's. Re-linking rewrites the metadata association only.
- Old `/media` files are deleted only *after* the replacement has downloaded successfully — the
  `DeleteSupersededMedia` discipline already in `RAWGService`.

### Phase 5 — documentation

CLAUDE.md (dense with RAWG), README, SECURITY-NOTES (new: Apicalypse injection; new egress hosts
`id.twitch.tv`, `api.igdb.com`, `images.igdb.com`).

---

## Traps — the ones that produce a working build

In the tradition of SQLITE-APPLIANCE-PLAN.md: every item here compiles and runs.

**1. Apicalypse injection — a genuinely new vulnerability class.** IGDB queries are a string body, not
parameters, and the search term goes into `search "…";`. An unescaped `"` lets a contributor append
clauses — `fields`, `where`, a second statement. Escape `\` and `"`, strip control characters and
newlines, before interpolation. Its own test class, and its own SECURITY-NOTES entry. RAWG's
query-string parameters had no equivalent, so this is new surface, not a port.

**2. The token cache versus "read settings per call".** The credentials must be read per call
(`SettingsService` has no cache, deliberately — see its class comment, and the bug it documents). The
*token* must be cached, or every search burns a Twitch round-trip. Resolve it by keying the cached
token on a hash of the credentials: change them in the admin UI and the cache misses on the next call.
Capturing credentials in a singleton constructor is precisely the failure `RAWGService` and
`FileService` already had once.

**3. `first_release_date` is a Unix epoch integer**, not a date string, and the target column is
declared `date`. Convert at ingest.

**4. IGDB `summary` is plain text; the column holds HTML.** Post-migration that column contains
copied RAWG HTML *and* new IGDB text, and it is rendered client-side with `MarkupString`. HTML-encode
and paragraphise IGDB summaries at ingest so the column stays uniformly "HTML, sanitised on the way
out" — otherwise a `<3` in a summary silently disappears, and worse things than `<3` are possible.
`GameMappings`' sanitiser stays exactly as it is.

**5. Scaffolder value generation on the new PK.** The existing RAWG tables scaffold to
`ValueGeneratedNever`, which is correct for them (ids are always supplied). `GameMetadata.id` is a
surrogate the database assigns, so it needs `ValueGeneratedOnAdd`. Check what the scaffolder actually
produces and, if it is wrong, fix it in `EFModel/DatabaseContextConfiguration.cs` — which lives
outside `Models/` so a re-scaffold cannot delete it. Get this wrong and the first IGDB game inserts
with id 0, and the second fails on a unique constraint.

**6. Migration 007 must contain no `BEGIN`/`COMMIT`.** `SchemaMigrator` wraps each numbered script in
its own transaction; only the baseline manages its own. Follow `005`, not `01_schema.sql`.

**7. Rate limiting.** 4 requests/second, 8 concurrent, `429` past that. Fine for the picker; the
Phase 4 bulk re-link needs a real throttle, and a 429 must back off rather than mark a game unmatched.

**8. `aggregated_rating` is not Metacritic.** It is IGDB's own 0–100 critic aggregate. Store it in a
neutrally named column and change the UI label — `metacritic 87` next to a number that is not
Metacritic's is a small lie that will be believed.

**9. Existing `/media` files must survive the migration untouched.** The copy moves paths, not files.
Assert it: count files in the media directory before and after an upgrade in the install test.

---

## Explicitly out of scope

Two things IGDB makes newly possible. Both are real improvements and neither belongs in this
migration:

- **IGDB covers are portrait box art** (`t_cover_big`, 3:4) — which is exactly the picture RAWG could
  not supply and the reason SteamGridDB is wired in at all. The existing fallback chain improves for
  free the moment covers are stored, but *offering IGDB as a candidate source in the box-art picker*
  is a separate change to `IBoxArtProvider`.
- **`multiplayer_modes` maps almost exactly onto GameTown's tag vocabulary** — `splitscreen`,
  `lancoop`, `offlinecoop`, `onlinecoop`. Suggesting quick-add tags from it is an obvious future
  feature. It must stay a *suggestion*: tags are the contributor's judgement, and `TagService` owns
  their identity.

---

## Decision 7 — the re-link never runs by itself

Recorded here rather than only in Phase 4, because it is the decision that keeps the two layers from
collapsing back into one.

It was tempting to auto-match exact titles on first start after an upgrade: most libraries would
silently heal and nobody would have to open anything. It is rejected, for three reasons that all
point the same way.

**It would put the network back in the upgrade path.** Layer 1's whole value is that it is offline
and deterministic — it cannot fail because a provider is down, which is the exact failure that
started this migration. An automatic re-link would reintroduce that dependency at the worst possible
moment, on someone else's machine, unattended.

**It would depend on credentials that do not exist yet.** The upgrade happens before the operator has
registered a Twitch application. So the automatic path's *normal* case is "no credentials, skipped",
and the code would exist to serve a case that almost never fires — while still being able to
half-succeed on the installs where it does.

**A half-finished automatic pass is worse than an untouched library.** An admin who opens the tool
sees exactly what is unmatched and decides. An admin whose library was partly re-linked at 4 a.m. by
a service restart sees a shelf where some games changed and some did not, with nothing recording why.

So: `provider = 'rawg'` is a permanently supported state, not a pending task. The library renders from
it indefinitely. The only thing that changes it is an administrator, in the admin interface, pressing
something.
