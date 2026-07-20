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
- Installed SDKs: `9.0.205`, `10.0.301`. Runtimes incl. `Microsoft.AspNetCore.App 10.0.9`. → .NET 10 upgrade is feasible.
- `dotnet-ef` global tool NOT installed — install before Phase 2c: `dotnet tool install --global dotnet-ef`.
- Current Aspire: `Aspire.AppHost.Sdk 9.0.0`, `Aspire.Hosting.AppHost 9.3.1`. Verify latest against NuGet before bumping (AppHost project format shifted across 9.x).

---

## Phase 1 — Package upgrades + .NET 10  ☐
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

## Phase 2 — SQL Server → PostgreSQL (DB-first)  ☐

### 2a. Author Postgres schema (replaces `.sqlproj`)  ☐
Translate `Database/Tables/*.sql` + post-deploy seed into Postgres DDL (new folder e.g. `Database/postgres/`); remove the SSDT `.sqlproj` from the solution.

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

### 2b. Stand up Postgres + run scripts  ☐
User creates the DB and runs DDL + seed (or provide connection details / run via `! psql` in-session).

### 2c. Re-scaffold EFModel from Postgres  ☐
Add `Npgsql.EntityFrameworkCore.PostgreSQL` (10.x) to `EFModel`; regenerate model:
```
dotnet ef dbcontext scaffold "<npgsql-connstring>" Npgsql.EntityFrameworkCore.PostgreSQL -o Models -c DatabaseContext -f
```
Update `efpt.config.json` to the Npgsql provider so VS EF Core Power Tools keeps working.
Casing note: scaffold emits explicit `HasColumnName`/`ToTable`, so Npgsql quotes+preserves the current `GameTownGame` / snake_case-RAWG names → API DTO mapping stays valid.

**Fallback if not standing up Postgres first:** hand-edit the model files for Npgsql instead of scaffolding (no longer strictly DB-first) — decide at that point.

### 2d. Wire API to Npgsql  ☐
`API/Startup/DependenciesConfig.cs`: `options.UseSqlServer(...)` → `options.UseNpgsql(...)`. Update `DefaultConnection` secret to Npgsql format. Aspire AppHost untouched (external Postgres).

**Gate (first runtime test):** launch via Aspire; seeded **`test` user logs in** end-to-end (exercises Npgsql + schema + seed + SHA256 hashing at once).

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
1. Re-clone the repo; open in the working dir.
2. Read this file + `CLAUDE.md`.
3. Re-set user-secrets for `API` (lost with the OS): `DefaultConnection` (Npgsql), `JwtSettings:*`, `RAWGApiKey`, `GameFilesPath`. See `API/appsettings.json` for the key list.
4. `dotnet tool install --global dotnet-ef` (needed for Phase 2c).
5. Continue from the first unchecked ☐.
