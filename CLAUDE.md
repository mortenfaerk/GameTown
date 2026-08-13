# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

GameTown is a .NET 10 solution for cataloguing and distributing games. Metadata is enriched from the external [IGDB](https://www.igdb.com) games API, cover art from [SteamGridDB](https://www.steamgriddb.com), uploaded game archives are stored on disk, and access is gated by JWT-based auth with `Admin`/`Contributor` roles. The frontend is a Blazor WebAssembly SPA that talks to a minimal-API backend.

**Metadata used to come from RAWG, which was retired.** That history is load-bearing rather than trivia — it is why the metadata tables carry a `provider` column instead of being shaped like whichever source is current, and why an installed library keeps rendering from a source that no longer exists. See `IGDB-MIGRATION-PLAN.md` and migration `007`.

Two things about a game are entered by people rather than imported, because no external source has them:

- **Box art.** RAWG had no cover field — its `background_image` was a wide promotional still, the wrong picture and the wrong shape for a shelf. (IGDB covers *are* portrait, so a re-linked game falls back to a real cover; the override still exists because a contributor's choice beats a catalogue's.) A contributor picks one from SteamGridDB (whose "grids" are 600×900 portrait covers), pastes a link, or uploads a file. **A Google Images search is not an option and should not be attempted**: Google's Custom Search JSON API is the only sanctioned route to image results, it is closed to new users, and it switches off on 1 January 2027. Bing's Image Search API was retired in August 2025.
- **Tags** — split screen, LAN, co-op, competitive, plus anything typed. Imported genres say what a game *is*; these say how it gets played, which is the question the shelf actually gets asked. (IGDB's `multiplayer_modes` overlaps this vocabulary and could seed suggestions one day. It must stay a *suggestion*: tags are a judgement, and `TagService` owns their identity.)

Both are stored locally in every case: box art is always downloaded onto the server and served from `/media`, never hot-linked.

A game's instructions can also be written **into its archive** as `GameTownGuide.txt`, so they are there after extracting rather than only on a web page nobody has open by then. **ZIP only, and that is a property of the formats, not a backlog item**: ZIP keeps its index at the end where it can be rewritten cheaply, TAR could be added the same way, 7z would need the external 7-Zip binary (the dependency class this appliance exists without) and RAR has no free writer at all.

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
dotnet run --project API --launch-profile https  # run just the API (it serves the app too)
```

`GameTownApp` has no standalone run step on purpose — the API serves its compiled bundle from its own wwwroot, so "the app" and "the API" are one process on one origin. Launching the WASM dev server on its own *appears* to work but the SPA then resolves its API address to that dev server, and every call comes back as `index.html` (`ExpectedStartOfValueNotFound, <`). See the comment in `Aspire/Aspire.AppHost/Program.cs`.

- API listens on `https://localhost:7188` (also plain-HTTP `http://localhost:5187`, the same port the installed appliance binds — see `install.sh`).
- API docs (Scalar UI) are at `/scalar/v1`; the `https` profile opens it on launch.
- **First run goes to `/setup`** — the server-rendered wizard that creates the first administrator (`API/Pages/Setup.cshtml`). It 404s once an admin exists, so it is only reachable on a fresh database. The Aspire dashboard's `gametown` row links all three: **GameTown**, **Setup (first run)** and **API docs (Scalar)** (`Aspire/Aspire.AppHost/Program.cs` sets them via `WithUrls`; the row shows the first two inline and collapses the rest behind a `+N` chip).
- The frontend has **no configured API URL and nothing hardcoded**: the API serves the SPA, so it resolves its API base from wherever it was loaded — `builder.HostEnvironment.BaseAddress` in `GameTownApp/Program.cs`. One published artifact therefore runs at any address (a LAN IP, a custom port, a reverse-proxied hostname) with no rebuild. There is no `api.gametowndev.com` or any other deployment host in the source.

### Tests

```bash
dotnet test Tests/GameTown.Tests/GameTown.Tests.csproj
```

203 tests, mostly HTTP-level against the real app booted through `WebApplicationFactory` on a throwaway SQLite database created from `Database/sqlite/*.sql` — the same files the application embeds and applies on a fresh install, so DDL/model drift fails here.

Requires the `sqlite3` CLI on the machine running the tests: the harness shells out to it so its view of the database stays independent of the EF model under test. The shipped application does not need it.

They are written against the bug classes this codebase has actually produced, all of which compiled and ran:

- **`ApiRoutingTests` asserts on `Content-Type`, not just status.** Under SPA-fallback hosting an unmatched route returns `200 text/html`, so a status-only assertion passes while the caller parses a web page as JSON. This is how `.Accepts<T>()` on GET routes went unnoticed.
- **`AuthenticationTests`** pins the three cookie settings that fail silently: not `Secure` (or the browser discards it over the LAN's plain HTTP), `SameSite=Lax` (the CSRF mitigation), and rejections as 401/403 rather than a 302 to HTML.
- **`SettingsTests`** boots with no IGDB credentials configured and asserts saved ones become visible to the running service — proof that the provider and `FileService` still read per call rather than capturing at startup. It matters more than it did for RAWG: the *token* derived from those credentials is legitimately cached (it lasts ~60 days), so this is exactly the shape of code where a stale value could outlive a settings change. `IgdbTokenProvider` keys its cache on a hash of the credentials for that reason.
- **`SchemaTests`** compares a fresh install against an upgraded one object-by-object, which is the only place baseline/migration drift would show. It also boots against a *populated* database pinned at the previous release's version and asserts the library survives field by field — "the migration runs" and "the migration runs without destroying anything" are different claims, and only the second matters to someone with games in there.
- **`FileContainmentTests`** covers `FileService.TryResolveWithin`, the check that keeps a stored path from escaping the archive directory.
- **`UploadTests` asserts the archive directory is empty after every rejected upload.** The endpoint streams the body straight to its final location, so the bytes are already on disk when a missing title or an exceeded size limit is discovered — a handler that forgets to clean up leaks a file per failed upload instead of failing harmlessly. It also pins that field order does not matter, since nothing in multipart guarantees one.
- **`UploadDeduplicationTests`** covers the SHA-256 guard against re-uploading an archive after an upload that only *appeared* to fail. The case that matters most is the negative one: games with no recorded hash (uploaded before migration 003) must not all match each other on `NULL`.
- **`AbandonedUploadTests`** boots a second app over the same data directory — a service restart, from the filesystem's point of view — and asserts the startup sweep removes `.part` files a killed process left behind while leaving finished archives alone.
- **`DirectoryProbeTests` / `SetupPathTests`** cover the archive-directory check: it writes and deletes a real file rather than reading permission bits (mode bits predict nothing on a CIFS mount), and the wizard validates *before* creating the administrator — `/setup` 404s once one exists, so rejecting the path afterwards would leave the operator with no wizard to fix it in.
- **`BoxArtTests` is mostly about refusal, not success.** Setting a cover from a URL is the only place the server fetches an address a user chose, and the appliance sits *inside* a home LAN — so "fetch this for me" reaches routers and metadata endpoints that nothing else can. It pins private-address and non-HTTP URLs being rejected, and that a stored file is named from its sniffed content rather than from the upload: these files are served back from the API's own origin, so an accepted SVG or HTML document would be stored XSS.
- **`ZipGuideWriterTests` reads every result back with `System.IO.Compression`, never with the writer's own parser.** Checking a hand-rolled ZIP writer against itself proves only self-consistency; what has to hold is that an independent implementation still opens the file and still finds every entry that was in it before. The failure being guarded is not "the guide is missing" — it is a contributor's multi-gigabyte archive being quietly corrupted by a feature that adds a text file to it. Note the awkward fixtures (a trailing comment, prepended data, a nested stored ZIP, 70,000 entries): each targets a way the backwards EOCD scan or the ZIP64 records can go wrong on real archives while passing on small ones.
- **`TagTests` pins that a tag's identity is its slug.** Tags are typed by hand, by several people, over months. If the text is the identity, the list fills with "Co-op"/"co op"/"COOP" and the filter bar stops being useful one duplicate at a time — the failure is gradual and never looks like a bug. It also pins that a two-tag filter means AND (an OR returns results, just the wrong ones) and that several new tags in one save get distinct ids, which is the `ValueGeneratedNever` trap below: coining *one* tag succeeds even when the mapping is broken.
- **`SanitizerTests`** pins what survives `GameMappings`' HTML sanitiser — formatting tags in, every attribute out, and the mXSS shape from the AngleSharp advisory stripped. Descriptions are community-editable and are rendered with `MarkupString` on an anonymous page, so this is an XSS gate, not a formatting preference. The column now holds two things — RAWG HTML carried across by 007 and IGDB summaries HTML-encoded at ingest — which is why sanitising happens on the way *out*.
- **`ApicalypseTests`** covers a vulnerability class that did not exist before IGDB. Its queries are a string body, not parameters, so a search term is interpolated between quotes in a body that also carries `fields`/`where`/`limit`. An unescaped quote closes the string and the rest parses as query language — and IGDB answers it rather than erroring, so there is nothing to notice. The tests pin the escaping *and* that ordinary titles (`Tom Clancy's`, `S.T.A.L.K.E.R.`) still come through intact, because an escaper that mangles those is safe and useless.

Prefer adding to these over writing a new harness.

## Required configuration (user secrets)

`API/appsettings.json` ships with a `"SetInSecrets"` placeholder for the connection string. That is now the **only** required setting — everything else is stored in the database and edited from *Administer → Settings* at runtime, so an unconfigured install still boots (it has to, or it could not serve its own setup page).

- `ConnectionStrings:DefaultConnection` — SQLite connection string, e.g. `Data Source=/var/lib/gametown/gametown.db`. **The directory holding that file is the data directory**: the database, the Data Protection keyring, uploaded archives and re-hosted media all default to living there, because it is the only location an in-place upgrade does not overwrite.

```powershell
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Data Source=/path/to/gametown.db" --project API
```

There is **no database initialization step**. Point the connection string at a path that does not exist and the application builds its own schema on first start: `SchemaMigrator` applies the baseline (embedded from `Database/sqlite/01_schema.sql` + `02_seed.sql`) when it finds a database with no tables, then replays the numbered migrations over it. A fresh install and an upgraded one therefore run the same sequence, which is what stops them drifting.

This also removed the dev-only `init-dev-db.ps1` bootstrap, and with it the wedge it existed to work around — an empty or missing `.db` used to be adopted as a pre-versioning install, stamped version 1, and then fail on the first migration because the baseline tables were never created.

Runtime settings (`GameFilesPath`, `IGDBClientId`, `IGDBClientSecret`, `BoxArtApiKey`, allowed upload types) live in the `Settings` table and are read **per request** by `SettingsService`. That is deliberate and fragile in one specific way: `IgdbProvider`, `FileService` and `SteamGridDbProvider` must keep reading them per call. The old `RAWGService` and `FileService` used to take these as constructor arguments resolved once at startup, which is exactly what made the settings UI look like it saved and changed nothing.

`IgdbTokenProvider` is the one deliberate exception, and it is not really one: it is a **singleton** that caches the Twitch OAuth token (valid ~60 days) but keys the cache on a **hash of the credentials that produced it**. The credentials are still read from the database per call; only the derived token is reused, and only while those credentials are current. Editing them in the admin UI misses the cache on the next call. Do not "simplify" this by capturing credentials in the constructor, and do not make it scoped — a scoped cache would re-authenticate on every request, which is the opposite mistake.

Both credentials are optional. Without IGDB credentials the app runs normally and metadata is entered by hand; without a SteamGridDB key the box-art *search* reports itself unavailable while uploading a file and pasting a link keep working — so the two paths deliberately share no dependency.

**IGDB has no API key.** It authenticates through a Twitch application: register one at `dev.twitch.tv/console/apps` (the account needs 2FA) for a Client ID and Client Secret. The pair is stored as two settings rows and both must be present — `GetIgdbCredentialsAsync` returns nulls unless both are, because a client id with no secret cannot authenticate and reporting it as configured turns a clear "not set up" into a confusing failure at the first search.

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
- **Services** (`API/Services/*.cs`) hold business logic and are scoped. Handlers stay thin (validate input, translate exceptions like `KeyNotFoundException` → `NotFound`) and push work into services. Metadata services live under `API/Services/Metadata/`. `IgdbTokenProvider` is the only singleton (see above).

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
- Table/column names are snake_case (`HasColumnName("external_id")` etc.); C# properties are PascalCase. Several many-to-many joins (users↔roles, metadata↔developers/genres/screenshots) are configured as implicit join entities in `OnModelCreating`.
- Primary keys are `Guid` for GameTown entities, generated **client-side**. The legacy RAWG entities reuse RAWG's own `int` ids (`ValueGeneratedNever`). The `Metadata*` entities have `int` **surrogate** keys the database assigns, with the provider's id in `external_id` — so all three need different treatment and the scaffolder gets two of them wrong. See `DatabaseContextConfiguration.cs`.
- Table and column identifiers are **double-quoted in the DDL** to preserve the original mixed casing (`"GameTownGame"`, `"PasswordHash"`). Keep new DDL quoted the same way, or the scaffolded model will drift.

#### Changing the schema

`Database/sqlite/01_schema.sql` is the **frozen baseline (version 1)**. Do not edit it to change the schema. Add a numbered migration in `Database/sqlite/migrations/` instead (`003_what_it_does.sql`), then re-scaffold. The schema is currently at **version 7**.

Fresh installs run the baseline and then every migration, exactly as an existing install does, so the two cannot drift apart. Keeping the baseline "current" *and* writing migrations is the alternative, and its failure mode — the two disagreeing — only ever appears on upgraded installs, never in development.

Migrations are embedded resources (`API.csproj`), applied by `SchemaMigrator` at startup before anything serves a request. Each script commits together with its `SchemaVersion` row, so a failure leaves the database at the previous version rather than half-applied. Write them to be safe to re-run (`IF NOT EXISTS`).

`ALTER TABLE ... ADD COLUMN` is the one statement that cannot honour that rule — SQLite has no `IF NOT EXISTS` for it and no conditional DDL — so a migration adding a column is not replayable. `003_game_archive_hash.sql`, `004_game_box_art.sql`, `006_game_guide.sql` and `007_metadata_provider.sql` document the exception where it occurs. Do not work around it by making `SchemaMigrator` swallow errors.

Migration `007` is also the worked example of a migration that moves *data*, not just shape. Three things about it are worth copying rather than re-deriving:

- **It is purely additive.** Nothing is dropped, and one reason is a hard constraint rather than caution: `GameTownGame.RAWGGameId` is named in a table-level FK, and SQLite refuses `DROP COLUMN` on a column a constraint references. Dropping `RAWGGames` alone is worse — with foreign keys on, every later write to `GameTownGame` fails against a missing FK target. Keeping both also keeps the release reversible.
- **It seeds the new surrogate ids from the old primary keys**, which turns re-pointing the whole library into `UPDATE "GameTownGame" SET "MetadataId" = "RAWGGameId"` — no join, no matching, no way to attach a game to someone else's metadata.
- **Its `INSERT ... SELECT`s are a no-op on a fresh install**, which is exactly what lets fresh and upgraded installs run the identical sequence.

**The installer needs no change when a migration is added.** Migrations are embedded resources applied by `SchemaMigrator` before the first request, so taking a new build *is* taking its schema; `install.sh` is only responsible for stopping the service, backing the database up and creating any new data subdirectory. `.github/workflows/install-test.yml` proves the whole path by rolling a real install back to the previous schema version — library and all — and re-running `install.sh` over it. Adding an "apply the migration" step to the installer would mean shipping the DDL and the `sqlite3` CLI again, and would give the schema two owners that can disagree.

`ALTER TABLE ... DROP COLUMN` and modern upsert syntax are safe to use: the SQLite version floor is the bundled `SQLitePCLRaw` native library, not whatever the host machine happens to have.

#### SQLite specifics that are easy to get wrong

SQLite is dynamically typed, so several things Postgres enforced are now conventions the code has to uphold. All four of these fail *silently*:

- **Declared column type names are load-bearing.** The scaffolder cannot infer a CLR type from SQLite storage, so it reads the declared name: `uniqueidentifier`→`Guid`, `datetime`→`DateTime`, `boolean`→`bool`. A bare `TEXT` column scaffolds to `string` and an `INTEGER` to `int`, quietly changing the model. (It will also guess `Guid` by sniffing existing row values — never rely on that; it makes scaffolding non-deterministic and yields `string` against an empty database.)
- **GUID literals in SQL must be UPPERCASE.** EF serialises `Guid` to uppercase `'D'`-format text and SQLite compares TEXT binary. Lowercase literals still *read* back fine, so the failure only surfaces later when an EF-inserted child row's FK does not match a hand-written parent key.
- **Foreign keys are off unless the connection enables them.** `API/Startup/SqliteConnectionString.cs` forces `Foreign Keys=True`; without it every FK is decorative and `ON DELETE CASCADE` never fires.
- **`ValueGeneratedNever` on Guid keys is a scaffolding artefact, not intent.** With no database default the scaffolder marks GameTown's Guid PKs as never-generated, which would insert `Guid.Empty`. `EFModel/DatabaseContextConfiguration.cs` restores `ValueGeneratedOnAdd` through the `OnModelCreatingPartial` hook — it lives outside `Models/` precisely so a re-scaffold cannot delete it.

`varchar(n)` lengths are also unenforced; the lengths survive only as comments in the DDL, so length validation is the application's job.

### Metadata integration & media
- `IGameMetadataProvider` / `IgdbProvider` fetch from IGDB; `GameMetadataService` persists what they return, resolving every related row against what is already stored or tracked so a second game sharing a studio does not collide on a primary key. `MetadataRelinkService` moves a library entry from a retired provider to the current one — **only ever driven by an admin**, never automatically.
- IGDB expands related records inline (`screenshots.image_id`), so a full game is **one POST**. The paginated screenshot loop RAWG needed has no successor.
- IGDB dates are Unix epoch seconds; `aggregated_rating` is a fractional 0-100 critic score stored in `critic_score` and labelled "critic score", not "Metacritic" — it is Metacritic only on rows carried over from RAWG.
- Remote images — covers, screenshots and box art alike — are **downloaded and re-hosted locally** through `MediaStore`, which writes into the *data* directory (not `wwwroot`, which an in-place upgrade deletes) and returns `/media/{guid}.ext`. `Program.cs` maps that request path onto it. This is the single most consequential decision in the codebase: it is the reason losing RAWG cost nobody a single image, and the reason migration 007 needed no network.
- **Every outbound image download goes through `ImageFetcher`, and nothing may bypass it.** It refuses non-HTTP schemes, refuses to follow redirects, connects only to public addresses (checked *after* DNS, and connected to directly so the resolution cannot change underneath), caps the body while reading, and derives the file extension by sniffing magic bytes rather than trusting the URL or the server's `Content-Type`. Each of those closes a distinct hole — see the class comment and SECURITY-NOTES.md. IGDB's URLs go through it too. That they are now built from a template against a fixed host (`images.igdb.com`) rather than taken from a community-editable field is **not** a reason to trust the bytes at the far end — "we built the URL ourselves" says nothing about what answers it.

### Box art and tags
- `IBoxArtProvider` / `SteamGridDbProvider` search for candidates; `BoxArtService` stores whatever is chosen. The provider is behind an interface because the choice of source is genuinely open (see the Overview on why Google Images is not one).
- `TagService` owns resolve-or-create-by-slug, whole-set replacement, and orphan cleanup. Quick-add tags are rows flagged `IsQuickAdd`, seeded by migration 005 — **not** a hardcoded list in the UI, so the vocabulary belongs to the library rather than to a build, and the cleanup has a principled reason to keep them.
- Tag filtering is AND across tags (each narrows), lives in `GTGamesService.BrowseAsync` behind both `/getPaged` and `/search`, and travels as `?tags=slug,slug` so a filtered shelf keeps a pasteable URL — the same reasoning that puts search in `?q=`.
- `ArchiveGuideService` / `ZipGuideWriter` write `GameTownGuide.txt` into a game's own ZIP. **Never replace `ZipGuideWriter` with `ZipArchiveMode.Update`** — it documents itself as holding the entire archive in memory and writing nothing until dispose, so a one-kilobyte text file costs a full multi-gigabyte read, allocate and rewrite. It would pass every test built on a small fixture. The writer instead *only appends*: a new index and end-of-central-directory record go on the end, the old ones become unreachable bytes, and no existing byte is moved. That also makes failure recoverable — the rollback is a truncate back to the original length.
- `POST /GTGames/Add` answers **201 with the new game's id**, not the 204 it once did. Tags and box art are set through their own endpoints and need something to address; without an id the add-game screen could not finish describing what it had just uploaded.

## Conventions
- Target framework is `net10.0` with nullable reference types and implicit usings enabled across all projects.
- GameTown game IDs are GUIDs passed as strings over the wire and parsed with `Guid.TryParse` in handlers (returning `400` on failure).
- Some user-facing error strings are in Danish (e.g. `"Kunne ikke generere token"`).
