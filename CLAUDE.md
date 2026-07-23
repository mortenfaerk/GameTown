# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

GameTown is a .NET 10 solution for cataloguing and distributing games. Metadata is enriched from the external [RAWG](https://rawg.io) games API, uploaded game archives are stored on disk, and access is gated by JWT-based auth with `Admin`/`Contributor` roles. The frontend is a Blazor WebAssembly SPA that talks to a minimal-API backend.

## Projects

| Project | SDK / Type | Role |
|---|---|---|
| `API` | `Microsoft.NET.Sdk.Web` | Minimal-API backend. Endpoints, services, DTOs, auth. |
| `GameTownApp` | `Microsoft.NET.Sdk.BlazorWebAssembly` | Blazor WASM SPA (uses Blazor.Bootstrap). |
| `EFModel` | classlib | EF Core DbContext + entities. **Scaffolded database-first** — see below. |
| `Database` | plain SQL scripts (not an MSBuild project) | Source of truth for the schema: `sqlite/01_schema.sql` + `sqlite/02_seed.sql`. `postgres/`, `Tables/` and `PostDeploymentScripts/` are the retired PostgreSQL and SQL Server DDL, kept for historical reference only. |
| `Aspire.AppHost` | .NET Aspire orchestrator | Launches `API` + `GameTownApp` together for local dev. |
| `Aspire.ServiceDefaults` | classlib | Shared Aspire config (OpenTelemetry, health checks, service discovery). |

## Running & building

The intended entry point for local development is the Aspire AppHost, which launches both the API and the Blazor app:

```powershell
dotnet run --project Aspire/Aspire.AppHost      # runs API + app together (https launch profiles)
dotnet build GameTown.slnx                       # build everything (slnx, not sln)
dotnet run --project API --launch-profile https  # run just the API
dotnet run --project GameTownApp                  # run just the frontend
```

- API listens on `https://localhost:7188` (also `http://localhost:5187`).
- API docs (Scalar UI) are at `/scalar/v1`; the `https` profile opens it on launch.
- The frontend resolves its API base URL by environment: `https://localhost:7188` in Development, `https://api.gametowndev.com` otherwise (`GameTownApp/Program.cs`).

### Tests

```bash
dotnet test Tests/GameTown.Tests/GameTown.Tests.csproj
```

55 tests, mostly HTTP-level against the real app booted through `WebApplicationFactory` on a throwaway SQLite database created from `Database/sqlite/*.sql` — the same files the installer uses, so DDL/model drift fails here.

They are written against the bug classes this codebase has actually produced, all of which compiled and ran:

- **`ApiRoutingTests` asserts on `Content-Type`, not just status.** Under SPA-fallback hosting an unmatched route returns `200 text/html`, so a status-only assertion passes while the caller parses a web page as JSON. This is how `.Accepts<T>()` on GET routes went unnoticed.
- **`AuthenticationTests`** pins the three cookie settings that fail silently: not `Secure` (or the browser discards it over the LAN's plain HTTP), `SameSite=Lax` (the CSRF mitigation), and rejections as 401/403 rather than a 302 to HTML.
- **`SettingsTests`** boots with no RAWG key configured and asserts a saved key becomes visible to the running service — proof that `RAWGService`/`FileService` still read per call rather than capturing at startup.
- **`SchemaTests`** compares a fresh install against an upgraded one object-by-object, which is the only place baseline/migration drift would show.
- **`FileContainmentTests`** covers `FileService.TryResolveWithin`, the check that keeps a stored path from escaping the archive directory.

Prefer adding to these over writing a new harness.

## Required configuration (user secrets)

`API/appsettings.json` ships with a `"SetInSecrets"` placeholder for the connection string. That is now the **only** required setting — everything else is stored in the database and edited from *Administer → Settings* at runtime, so an unconfigured install still boots (it has to, or it could not serve its own setup page).

- `ConnectionStrings:DefaultConnection` — SQLite connection string, e.g. `Data Source=/var/lib/gametown/gametown.db`. **The directory holding that file is the data directory**: the database, the Data Protection keyring, uploaded archives and re-hosted media all default to living there, because it is the only location an in-place upgrade does not overwrite.

```powershell
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Data Source=/path/to/gametown.db" --project API
```

Then **initialize the database once** before the first run:

```powershell
./init-dev-db.ps1        # -Force to wipe and rebuild from the baseline
```

This is the dev-only counterpart to `install.sh`'s fresh-install branch. It reads the db path from user secrets, creates the data directory (`games/`, `media/`, `keys/`) and applies `Database/sqlite/01_schema.sql` + `02_seed.sql`. It is needed because the `dotnet run`/Aspire path has **no baseline-creation step** — `Program.cs` only runs the numbered migrations (`SchemaMigrator`); the baseline is otherwise created only by `install.sh` (production) or the test harness. Point the app at an empty or missing `.db` and it wedges: SQLite auto-creates an empty file, `SchemaMigrator` assumes a pre-versioning install and stamps it version 1, and the first migration then fails on the missing baseline tables. (Keep new `.ps1` scripts ASCII-only — PowerShell 5.1 reads UTF-8-without-BOM as ANSI, turning em-dashes into smart quotes that break parsing.)

Runtime settings (`GameFilesPath`, `RAWGApiKey`, allowed upload types) live in the `Settings` table and are read **per request** by `SettingsService`. That is deliberate and fragile in one specific way: `RAWGService` and `FileService` must keep reading them per call. They used to take these as constructor arguments resolved once at startup, which is exactly what made the settings UI look like it saved and changed nothing.

The RAWG key is optional — without one the app runs normally and metadata is entered by hand.

## HTTPS development certificate (needed on Linux)

The SPA and the API are separate origins, so the browser must trust the ASP.NET Core dev certificate
before the app can call the API. Untrusted, the symptom is misleading: the page itself loads (you can
click through its warning) but every `fetch` to the API dies with
`TypeError: NetworkError when attempting to fetch resource` — a cross-origin `fetch` gets no
click-through prompt.

```bash
dotnet dev-certs https --trust     # NOT with sudo — trust is per-user
dotnet dev-certs https --check --trust
```

Run it **as your own user**. Under `sudo` it trusts a certificate for root, which is not the store
your browser reads. Then fully quit and reopen the browser — the NSS store at `~/.pki/nssdb` is only
read at startup, so a reload is not enough. `certutil` (package `nss`) must be present for the
browser store to be updated.

`--check --trust` reporting *"none of them is trusted"* while the browser works is normal: that check
uses the **OpenSSL** store, which is separate. .NET-to-.NET calls (including Aspire's dashboard
telemetry, which otherwise logs `UntrustedRoot` gRPC errors) need:

```bash
export SSL_CERT_DIR="$HOME/.aspnet/dev-certs/trust:/etc/ssl/certs"
```

Keep `/etc/ssl/certs` in that list, or every other TLS connection on the machine stops validating.
GUI-launched IDEs do not read your shell profile — set it in the run configuration or via
`~/.config/environment.d/`.

## Architecture notes

### API composition
- `Program.cs` is deliberately thin: it calls `builder.AddDependencies()` (all DI + auth wiring lives in `API/Startup/DependenciesConfig.cs`) then registers endpoint groups via `app.Add*Endpoints()` extension methods.
- **Endpoints** (`API/Endpoints/*.cs`) are static classes, each exposing an `Add…Endpoints(this WebApplication)` extension that maps a `MapGroup` and delegates to private static handlers. To add a route, add it to the relevant group and register the group in `Program.cs`. `TestEndpoints` is only mapped when `ASPNETCORE_ENVIRONMENT == Development`.
- **Services** (`API/Services/*.cs`) hold business logic and are scoped. Handlers stay thin (validate input, translate exceptions like `KeyNotFoundException` → `NotFound`) and push work into services. `RAWGService` and `FileService` are registered with factory lambdas because they take primitive constructor args (RAWG key / files path).

### Auth flow (cookie)
- Login (`/auth/login`) validates credentials and signs in an **HttpOnly `gametown_auth` cookie** (`SameSite=Lax`, `SecurePolicy=SameAsRequest`, sliding expiration). `/auth/logout` signs out; `GET /auth/me` returns the current user or 401. There is no token, no `/auth/refresh` and no `RefreshTokens` table — sliding expiration replaced all three.
- Authorization policies are defined in `DependenciesConfig`: `Admin` requires role `Admin`; `Contributor` accepts `Contributor` **or** `Admin`. Protect endpoints with `.RequireAuthorization("Contributor")`.
- Frontend auth is in `GameTownApp/Services/AuthService.cs` (an `AuthenticationStateProvider` that asks `/auth/me` rather than parsing a token, because the cookie is unreadable from script). The DI `HttpClient` keeps a single `CookieHandler`; `AuthService` still has its own `HttpClient` for the auth calls.
- **Four cookie settings are load-bearing and each fails silently.** `SecurePolicy` must stay `SameAsRequest` or the browser drops the cookie over the plain-HTTP LAN default; `OnRedirectToLogin`/`OnRedirectToAccessDenied` must return 401/403 or `fetch` follows a redirect and parses a login page as JSON; and Data Protection keys must stay persisted to the data directory or every restart signs everyone out.
- **CSRF is now a live threat class** — see SECURITY-NOTES.md. `SameSite=Lax` is the mitigation, and it only holds while no `GET` mutates state.

### Data layer — EFModel is generated, do not hand-edit
- `EFModel/Models/` (including `DatabaseContext.cs` and the entity classes) is **auto-generated** from a live SQLite database (files carry an `<auto-generated>` header). Changing the schema means editing `Database/sqlite/*.sql`, applying it to a database file, and re-scaffolding — not editing these files by hand:

  ```bash
  dotnet ef dbcontext scaffold "Data Source=/path/to/gametown.db" Microsoft.EntityFrameworkCore.Sqlite \
      -o Models -c DatabaseContext -f --project EFModel --startup-project EFModel
  ```

  **After every re-scaffold, delete the generated `OnConfiguring` override in `DatabaseContext.cs`** — it hardcodes the connection string into source. The connection comes from configuration via `AddDbContext` in `DependenciesConfig.cs`.
- Table/column names use RAWG's snake_case (`HasColumnName("background_image")` etc.); C# properties are PascalCase. Several many-to-many joins (users↔roles, games↔developers/genres/screenshots) are configured as implicit join entities in `OnModelCreating`.
- Primary keys are `Guid` for GameTown entities, generated **client-side**; RAWG entities reuse RAWG's own `int` ids (`ValueGeneratedNever`).
- Table and column identifiers are **double-quoted in the DDL** to preserve the original mixed casing (`"GameTownGame"`, `"PasswordHash"`). Keep new DDL quoted the same way, or the scaffolded model will drift.

#### Changing the schema

`Database/sqlite/01_schema.sql` is the **frozen baseline (version 1)**. Do not edit it to change the schema. Add a numbered migration in `Database/sqlite/migrations/` instead (`003_what_it_does.sql`), then re-scaffold.

Fresh installs run the baseline and then every migration, exactly as an existing install does, so the two cannot drift apart. Keeping the baseline "current" *and* writing migrations is the alternative, and its failure mode — the two disagreeing — only ever appears on upgraded installs, never in development.

Migrations are embedded resources (`API.csproj`), applied by `SchemaMigrator` at startup before anything serves a request. Each script commits together with its `SchemaVersion` row, so a failure leaves the database at the previous version rather than half-applied. Write them to be safe to re-run (`IF NOT EXISTS`).

`ALTER TABLE ... DROP COLUMN` and modern upsert syntax are safe to use: the SQLite version floor is the bundled `SQLitePCLRaw` native library, not whatever the host machine happens to have.

#### SQLite specifics that are easy to get wrong

SQLite is dynamically typed, so several things Postgres enforced are now conventions the code has to uphold. All four of these fail *silently*:

- **Declared column type names are load-bearing.** The scaffolder cannot infer a CLR type from SQLite storage, so it reads the declared name: `uniqueidentifier`→`Guid`, `datetime`→`DateTime`, `boolean`→`bool`. A bare `TEXT` column scaffolds to `string` and an `INTEGER` to `int`, quietly changing the model. (It will also guess `Guid` by sniffing existing row values — never rely on that; it makes scaffolding non-deterministic and yields `string` against an empty database.)
- **GUID literals in SQL must be UPPERCASE.** EF serialises `Guid` to uppercase `'D'`-format text and SQLite compares TEXT binary. Lowercase literals still *read* back fine, so the failure only surfaces later when an EF-inserted child row's FK does not match a hand-written parent key.
- **Foreign keys are off unless the connection enables them.** `API/Startup/SqliteConnectionString.cs` forces `Foreign Keys=True`; without it every FK is decorative and `ON DELETE CASCADE` never fires.
- **`ValueGeneratedNever` on Guid keys is a scaffolding artefact, not intent.** With no database default the scaffolder marks GameTown's Guid PKs as never-generated, which would insert `Guid.Empty`. `EFModel/DatabaseContextConfiguration.cs` restores `ValueGeneratedOnAdd` through the `OnModelCreatingPartial` hook — it lives outside `Models/` precisely so a re-scaffold cannot delete it.

`varchar(n)` lengths are also unenforced; the lengths survive only as comments in the DDL, so length validation is the application's job.

### RAWG integration & media
- `RAWGService` calls the RAWG REST API (via RestSharp + Newtonsoft.Json), paginates screenshots, and `AddGameToDb` upserts a RAWG game plus screenshots into the local DB.
- Screenshot images are **downloaded and re-hosted locally**: `DownloadAndReplaceImageUrlsAsync` writes them to `API/wwwroot/media/` and rewrites URLs to `/media/{guid}.ext` served as static files.

## Conventions
- Target framework is `net10.0` with nullable reference types and implicit usings enabled across all projects.
- GameTown game IDs are GUIDs passed as strings over the wire and parsed with `Guid.TryParse` in handlers (returning `400` on failure).
- Some user-facing error strings are in Danish (e.g. `"Kunne ikke generere token"`).
