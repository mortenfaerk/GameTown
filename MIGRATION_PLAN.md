# GameTown — Migration & GUI Wiring Plan

> Working plan for: migrate infra SQL Server → PostgreSQL, upgrade all packages to .NET 10,
> then wire up the remaining GUI against the existing API.
> Saved 2026-07-20 before an OS reinstall. Pick up from whichever phase is unchecked.

## Context
GameTown is a "Plex, but for games" system for sharing freeware games over a local LAN.
.NET solution, 6 projects. See `CLAUDE.md` for full architecture. Key facts for this work:

- **Backend** `API` — minimal API, endpoints as `Add*Endpoints` extension methods, DI/auth in `API/Startup/DependenciesConfig.cs`.
- **Frontend** `GameTownApp` — Blazor WASM, Blazor.Bootstrap.
- **Data** `EFModel` — EF Core, **scaffolded database-first** (EF Core Power Tools, `efpt.config.json`). Do not hand-edit generated files under normal workflow.
- **Schema** `Database` — SQL Server `.sqlproj` (SSDT) + post-deploy seed. **SSDT is SQL-Server-only and must be retired** (see Phase 2).
- Auth: JWT in body + HttpOnly `refresh_token` cookie; roles `Admin` / `Contributor`.
- No test project exists. Verification = build + login smoke test + manual GUI driving.

## Decisions made (2026-07-20)
1. **Schema source of truth:** DB-first. Postgres SQL scripts define the schema; EFModel is *re-scaffolded* from a live Postgres DB. (The `.sqlproj` is replaced by plain Postgres `.sql` scripts — SSDT can't target Postgres.)
2. **Postgres hosting:** external — user runs Postgres; connection string stays in user-secrets. Aspire AppHost is NOT changed to host the DB.
3. **Browsing access:** stays public. GTGames GET / download / search / getPaged have no `RequireAuthorization` by design (anyone on LAN browses+downloads; login only gates contributing/admin). Build the browse UI as public.
4. **GUI build order:** admin/contributor tooling FIRST, then the player-facing browse experience.

## Environment verified
> **Re-verified 2026-07-22, after the OS reinstall (now CachyOS/Linux).** The pre-reinstall
> readings below were stale — .NET 9 is gone entirely.

- Installed: **SDK `10.0.110` only**; runtimes `Microsoft.NETCore.App 10.0.10` + `Microsoft.AspNetCore.App 10.0.10`.
  There is **no .NET 9 runtime**, so the pre-upgrade `net9.0` build could compile (SDK 10 restores
  net9.0 targeting packs from NuGet) but **could not run**. This made Phase 1 mandatory, not optional.
- `aspnet-runtime-10.0` installed. **`aspnet-targeting-pack-10.0` is also required** — without it the
  `Microsoft.AspNetCore.App` FrameworkReference in `Aspire.ServiceDefaults` fails with
  `NETSDK1226: Prune Package data not found`. (Workaround if ever needed: `-p:AllowMissingPrunePackageData=true`.)
- `dotnet-ef` **installed** (`10.0.10`). Needs `~/.dotnet/tools` on PATH.
- Aspire went `9.3.1` → **`13.4.6`** (a 4-major jump; the AppHost API surface used here is unchanged).
- Postgres is **external at `192.168.1.236:5432`**, db `gametown_dev`, user `gametown_dev`. No local
  `psql` client and no docker — DDL is applied via a throwaway Npgsql runner instead.
- `GameFilesPath` must now be a **Linux** path (`appsettings.json` still ships the Windows default
  `C://repos//GameFiles`); set to `/home/mortenfr/GameFiles` in user-secrets.
- **RAWG decision (2026-07-22):** keep RAWG, do not refactor to another metadata API. 7 of the 13
  tables are RAWG-shaped and keyed on RAWG's own int ids; the only serious free alternative (IGDB)
  needs Twitch OAuth and a different data model — a schema-deep rewrite for zero migration benefit.
  A placeholder `RAWGApiKey` is set so startup validation passes; drop the real key in when available.

---

## Phase 1 — Package upgrades + .NET 10  ☑ (done 2026-07-22)

**Outcome:** all 5 projects on `net10.0`, `dotnet build GameTown.sln` green (0 errors), and the
`NU1902` OpenTelemetry advisory is cleared. Versions landed: ASP.NET Core/EF Core `10.0.10`,
Aspire `13.4.6`, Scalar `2.16.16`, RestSharp `114.0.0`, Blazor.Bootstrap `3.5.0`, OpenTelemetry `1.17.0`.

**One code change was required** (not just version bumps): `Microsoft.AspNetCore.OpenApi` 10.x moves to
OpenAPI.NET **v2**, which flattens `Microsoft.OpenApi.Models` → `Microsoft.OpenApi` and makes security
schemes interface-based. `API/Startup/OpenApiConfig.cs` was ported: `Dictionary<string, OpenApiSecurityScheme>`
→ `Dictionary<string, IOpenApiSecurityScheme>`, and the `OpenApiSecurityScheme { Reference = ... }` key
→ `new OpenApiSecuritySchemeReference("Bearer", document)`. Scalar's 11-minor jump needed no changes.

**Security pin:** `Microsoft.AspNetCore.OpenApi` 10.0.10 pulls `Microsoft.OpenApi` **2.0.0**, which carries
a *high*-severity advisory (GHSA-v5pm-xwqc-g5wc). `API.csproj` now pins `Microsoft.OpenApi` **2.11.0**
(latest 2.x — 3.x is a breaking API change). Build is clean of `NU` advisories.

**New .NET 10 deprecations to clean up later** (warnings only, nothing broken):
- `ASPDEPR002` — `.WithOpenApi()` is deprecated across the endpoint groups.
- `SYSLIB0060` — the `Rfc2898DeriveBytes` *constructor* in `API/Helpers/ApiKeyHelper.cs` is obsolete;
  migrate to the static `Rfc2898DeriveBytes.Pbkdf2(...)`. **Must preserve** PBKDF2-SHA256 / 100k
  iterations / 32-byte output or every existing password hash breaks.

Do first, in isolation, so it stays bisectable from the Postgres swap. Gate is compile-only
(no Postgres up yet, no SQL Server to verify against).

Bump all 5 projects `net9.0 → net10.0` and update packages:

| Project | Changes |
|---|---|
| `API` | TFM → net10.0; `Microsoft.AspNetCore.Authentication.JwtBearer` + `Microsoft.AspNetCore.OpenApi` 9.0.6 → 10.x; `Scalar.AspNetCore`, `RestSharp`, `Newtonsoft.Json` → latest |
| `GameTownApp` | TFM → net10.0; `Microsoft.AspNetCore.Components.WebAssembly*` 9.0.6 → 10.x; `Blazor.Bootstrap` → latest |
| `EFModel` | TFM → net10.0; EF Core 9.0.3 → 10.x (provider swapped in Phase 2) |
| `Aspire.ServiceDefaults` | TFM → net10.0; OpenTelemetry + resilience + service-discovery → latest |
| `Aspire.AppHost` | TFM → net10.0; `Aspire.AppHost.Sdk` + `Aspire.Hosting.AppHost` → latest (verify version on NuGet) |

**Gate:** `dotnet build GameTown.sln` succeeds (still on SQL Server).

---

## Phase 2 — SQL Server → PostgreSQL (DB-first)  ☑ (done 2026-07-22)

### 2a. Author Postgres schema (replaces `.sqlproj`)  ☑ written, ☐ not yet applied
Written to `Database/postgres/01_schema.sql` (13 tables) and `02_seed.sql`. All identifiers are
double-quoted to preserve SQL Server casing, per the casing note in 2c.
Seed hash/salt copied **verbatim** — verified against `API/Helpers/ApiKeyHelper.cs`, which uses
PBKDF2-SHA256 (100k iters) and compares uppercase hex with ordinal `==`, so any regeneration
would break the Phase 2d login gate.
**Still to do:** remove the SSDT `.sqlproj` from the solution once the DDL is proven against the live DB.

**Type mappings** (audited from current schema + model):
- `uniqueidentifier` → `uuid`
- `NVARCHAR(n)` / `NVARCHAR(MAX)` → `varchar(n)` / `text`
- `BIT` → `boolean`
- `FLOAT` → `double precision`
- `DATETIME2` → `timestamptz`
- `Rawggame.Updated` mapped `HasColumnType("datetime")` → **Postgres has no `datetime`**, use `timestamp`. (`released` = `date` is fine.)

**Default-value SQL** (all break on Postgres):
- `newid()` / `NEWID()` → `gen_random_uuid()`
- `GETDATE()` → `now()`
- `SYSUTCDATETIME()` → `now()`

**Seed:** roles (`Admin`, `Contributor`) + dev `test` user with existing fixed GUIDs and SHA256 hash/salt (keep GUIDs identical so JWT/role logic is unaffected). Source of the seed values: `Database/PostDeploymentScripts/Script.PostDeployment.sql`.
- Roles: Admin `99ffbcba-6c26-416f-b996-33e8a0b4c6ef`, Contributor `37a3c94f-b2e0-46ac-a60b-2b9eb09c3a14`.
- User `test` `8f50b277-0b2d-4245-b686-e9c77a32b966`, mapped to Admin role.

### 2b. Stand up Postgres + run scripts  ☑ (done 2026-07-22)
Applied against **PostgreSQL 18.4** at `192.168.1.236:5432` / db `gametown_dev`. All 12 tables created,
roles + `test` user seeded and verified by query.
No local `psql` and no docker, so the scripts were executed with a throwaway Npgsql console runner
(kept in the session scratchpad, not committed). Reuse that approach for future DDL.
> Gotcha hit: the `gametown_dev` role was initially created without LOGIN
> (`FATAL 28000`); fixed with `ALTER ROLE gametown_dev WITH LOGIN PASSWORD '…'`.

### 2c. Re-scaffold EFModel from Postgres  ☐
Add `Npgsql.EntityFrameworkCore.PostgreSQL` (10.x) to `EFModel`; regenerate model:
```
dotnet ef dbcontext scaffold "<npgsql-connstring>" Npgsql.EntityFrameworkCore.PostgreSQL -o Models -c DatabaseContext -f
```
Update `efpt.config.json` to the Npgsql provider so VS EF Core Power Tools keeps working.
Casing note: scaffold emits explicit `HasColumnName`/`ToTable`, so Npgsql quotes+preserves the current `GameTownGame` / snake_case-RAWG names → API DTO mapping stays valid.

**Status: ☑ done 2026-07-22.** Re-scaffolded from the live Postgres DB. Round-trip diff vs. the old
SQL Server model: **identical file set, identical DbSet names, identical `ToTable`/`HasColumnName`
mappings, identical entity property names** — the casing strategy in 2a worked. Two intentional drifts:

1. **Nullability** — many `string` became `string?`. This is *not* schema drift: the old model was
   generated with EF Power Tools' `UseNullableReferences: false`, while `dotnet ef` honours the
   project's `<Nullable>enable</Nullable>`. The new annotations match the `.sql` source of truth.
   `efpt.config.json` has been set to `UseNullableReferences: true` so Power Tools now agrees.
2. **`Rawggame.Released`: `DateTime?` → `DateOnly?`** — Npgsql maps `date` natively. Handled at the
   DTO boundary (`entity.Released?.ToDateTime(TimeOnly.MinValue)`) so the JSON contract and the
   frontend `RAWGGameViewModel` are unchanged. `efpt.config.json` set to `UseDateOnlyTimeOnly: true`.

`efpt.config.json` table names were rewritten `[dbo].[X]` → `public.X`.

### 2d. Wire API to Npgsql  ☑ (done 2026-07-22)
`DependenciesConfig.cs` now calls `options.UseNpgsql(...)`; `DefaultConnection` secret is in Npgsql
format. Aspire AppHost untouched (external Postgres), as decided.

> **Package pin required:** Npgsql 10.0.3 depends on `Microsoft.EntityFrameworkCore.Relational`
> **10.0.4**, while EF Core is on 10.0.10. This surfaces only as an MSB3277 *warning* at build time
> but throws `FileNotFoundException: …Relational, Version=10.0.10.0` at the first query. Fixed by
> referencing `Microsoft.EntityFrameworkCore.Relational` 10.0.10 directly in `EFModel.csproj`.
> Re-check this pin whenever either package is bumped.

**Gate: ☑ PASSED.** `POST /auth/login` with the seeded `test` user → **HTTP 200**, JWT issued with
`role=Admin` and the correct user GUID, plus an HttpOnly/Secure/SameSite=None `refresh_token` cookie.
Wrong password → **401**. The refresh token row was persisted to Postgres (verified by query), so
reads *and* writes are proven: Npgsql + schema + seed + PBKDF2 + JWT + role mapping all work end-to-end.

**Dev credentials:** the seeded user is `test` / `123456`.

**Additional runtime verification (2026-07-22)** — beyond the login gate:
- `GET /GTGames/getPaged/1/10` and `GET /GTGames/search/?query=a&page=1&pageSize=10` → **200 `[]`**.
  Empty results, but the queries execute, so `GameTownGame` + the RAWG mappings and the
  `Released` DateOnly boundary are exercised, not just compiled.
- `GET /users/getAll` **without** a token → **401**; **with** `Authorization: Bearer <token>` → **200**.
  This proves JWT *validation* (not just issuance) against the rotated key, plus the `Admin` policy.
  The response includes a populated `roles[]` array, which confirms the **implicit many-to-many
  `GameTownUsers_Roles` join** survived re-scaffolding — the mapping CLAUDE.md flags as fragile.
- **DB constraint audit** (queried from `pg_constraint`, since the model diff cannot see these):
  all 10 FKs present; `FK_RefreshTokens_Users` is **CASCADE** as in SQL Server; every other FK is
  NO ACTION, matching the original; `GameTownUsers.Username` UNIQUE survived (auto-renamed by
  Postgres to `GameTownUsers_Username_key`). All `HasMaxLength` values match the old model.

**RAWG verified (real key set 2026-07-22):** `GET /meta/searchMetadata?query=doom&page=1&pageSize=3`
and `GET /meta/getGame/2454` both return **200** with live RAWG data.

**All 12 tables + all 3 implicit m2m joins are now proven against Postgres.** Since no endpoint
persists RAWG data (see the `AddGameToDb` note in Phase 3c), the RAWG tables were exercised directly
through the scaffolded `DatabaseContext` inside a transaction that was then rolled back:
insert + read-back of `Rawggame` with `Developers` / `Genres` / `Screenshots` all round-tripped,
`DateOnly`/`double`/`bool`/`timestamp` mapped correctly, and a `GameTownGame` row got a server-side
`gen_random_uuid()` PK plus a working FK to `RAWGGames`. DB left clean (all counts back to 0).

**Not yet runtime-verified:** the Aspire AppHost was never launched — Aspire 13.4.6 is proven to
*build*, not to orchestrate. The API was run directly via `dotnet run --project API`.

---

## Phase 3 — GUI wiring (admin/contributor first, then browse)  ☐
All API endpoints already exist; this is pure frontend. Current frontend gaps: `GamesService`
only has the 2 RAWG metadata calls; frontend `UserService` is an empty stub; `AddGame.razor`
is an empty `<h3>` stub; `UserManagement.razor` just renders game search; NavMenu minimal; no logout UI.

### 3a. Contracts / view models  ☐
Add frontend types for API responses missing on the client: `ResponseGameTownGameDTO`, `UserDTO`, `RoleDTO`, and the various result objects. (Consider a shared Contracts project vs. hand-copied models.)

### 3b. Admin — user & role management  ☐  (`UserManagement.razor`, frontend `UserService`)
Endpoints (all `Admin`): users `getAll` / `add` / `get` / `update` / `delete`, `addUserToRole` / `removeUserFromRole`; roles `getAllRoles` / `addRole` / `updateRole` / `deleteRole`.

### 3c. Contributor — add/edit/delete games  ☐  (`AddGame.razor`, extend `GamesService`)
> **Backend gap found 2026-07-22:** `RAWGService.AddGameToDb` (`API/Services/RAWGService.cs:83`)
> **has no callers anywhere in the solution.** `GET /meta/getGame/{id}` only fetches from RAWG and
> returns the DTO — it never persists. So nothing currently writes RAWG metadata (or screenshots, or
> the developer/genre joins) into the DB. Wiring this into the add-game flow is part of 3c, not an
> optional extra: without it a saved `GameTownGame.RAWGGameId` would reference a non-existent
> `RAWGGames` row and violate `FK_GameTownGame_Games`.
> The persistence mapping itself is verified working (see Phase 2d), so this is pure wiring.
- Upload form: file (`.zip/.rar/.7z`) + Title + HowTo + RAWG metadata picker (reuse `SearchGames` autocomplete → sets `RAWGGameId`) → `POST /GTGames/Add` (multipart, `Contributor`).
- Edit: `PATCH /GTGames/update`. Delete: `DELETE /GTGames/{id}`.

### 3d. Player-facing browse (the "Plex" core, public)  ☐
- Library grid on Home (`GET /GTGames/getPaged/{page}/{pageSize}`), title search (`GET /GTGames/search/`).
- Detail page: metadata + screenshots + download button (`GET /GTGames/download/{id}`) + HowTo.

### 3e. Shell polish  ☐
Role-based `NavMenu` links (`AuthorizeView`), logout action (service method exists, no UI), tidy placeholder Home.

### Opportunistic fix (not a separate task)
`AddGame` saves uploads to `GameFilesPath`, but `DownloadGame`/delete look under `wwwroot/games` — path mismatch that breaks download/delete. Fix while wiring the download flow (Phase 3d).

---

## To resume in a fresh session
Phases 1 and 2 are complete — **the app builds on .NET 10 and runs against PostgreSQL.**
Next unchecked work is **Phase 3 (GUI wiring)**, starting at 3a.

1. Open the repo in the working dir; read this file + `CLAUDE.md`.
2. User-secrets for `API` are already set (Npgsql `DefaultConnection`, rotated `JwtSettings:*`,
   placeholder `RAWGApiKey`, `GameFilesPath=/home/mortenfr/GameFiles`). Verify with
   `dotnet user-secrets list --project API`.
3. Smoke test: `dotnet run --project API --launch-profile https`, then log in as `test` / `123456`.
4. Continue from the first unchecked ☐ in Phase 3.

### Known outstanding items
- **`RAWGApiKey` is set and verified working** (real key, 2026-07-22). Decision to stay on RAWG is
  recorded under "Environment verified".
- ~~The SSDT `.sqlproj` is still in the solution~~ — **done 2026-07-22:** `Database/Database.sqlproj`
  deleted and dropped from the solution. `Database/Tables/*.sql` and `PostDeploymentScripts/` are kept
  as the historical record of the SQL Server schema. (`Database/Database.sqlproj_backup` is still
  tracked and is now equally dead — delete whenever.)
- **The solution is now `GameTown.slnx`** (the XML solution format), migrated via `dotnet sln migrate`.
  `GameTown.sln` has been deleted — build with `dotnet build GameTown.slnx`.
