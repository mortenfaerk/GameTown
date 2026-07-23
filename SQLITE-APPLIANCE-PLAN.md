# Plan: SQLite + single-origin + distributable appliance

Turning GameTown from "one instance I administer" into "an app other people install on their LAN
from a script." Four decisions are already settled:

1. **SQLite replaces PostgreSQL** as the primary database.
2. **Hosted WASM, single origin** — the API serves the SPA from its own `wwwroot`.
3. **Config is runtime-editable**, stored in the database, edited from an admin settings GUI that
   lives **inside the SPA** alongside the existing user-management console. Only the first-run wizard
   is server-rendered, because it alone has to run before the app is configured.
4. **No Blazor Web App / mixed render modes.** Plain Razor Pages give us server-side config work
   without dual `AuthenticationStateProvider`s, prerender double-execution, or a second client project.

Phases are ordered so each one is verifiable against a working app before the next starts.

---

## Status

Work is on branch `sqlite-appliance`. The build is green at every commit.

| Phase | State |
|---|---|
| 1 — SQLite | **Done**, verified (`5550f2c`, `a5bfa84`) |
| 2a — single origin | **Done**, verified (`4e9250c`) |
| 2b — cookie auth | Not started |
| 3 — config + settings GUI | Not started |
| 4 — first run + install | Not started |
| 5 — schema upgrades | Not started |

**Resume at 2b.** Everything below Phase 2's "Swap JWT for ASP.NET Core cookie auth" heading is
still outstanding; CORS, both delegating handlers, the `RefreshTokens` table and the JWT bearer
setup are all still in place and still working, so the tree is coherent rather than half-migrated.

Two things learned in Phase 1 that the later phases should reuse:

- **SQLite's failure mode is silence.** Every trap found so far (foreign keys off, `ValueGeneratedNever`
  on Guid keys, scaffolder type inference, GUID text casing) produced a *working* build that would
  have corrupted data later. Assume the same of anything new and prove it with a probe.
- **A throwaway verification harness paid for itself repeatedly.** It caught the FK/cascade behaviour
  and would have caught the un-wired WAL call had it asserted on `journal_mode`. There is still no
  test project; the harness lives only in scratch. Wiring an equivalent into the repo is worth doing
  before Phase 3 adds settings that can be edited at runtime.

---

## Phase 1 — PostgreSQL → SQLite

The schema is a good fit: 11 tables, no `jsonb`, no arrays, no full-text search. Search is already
`ToLower().Contains()` (`API/Services/GTGamesService.cs:176-185`), which translates to `LIKE`.

Keep the double-quoted identifiers in the new DDL. SQLite honours them, and it is what keeps the
scaffolded model stable — the same invariant CLAUDE.md already records.

### The five things that actually bite

**1. `gen_random_uuid()` has no SQLite equivalent** — verify how the re-scaffolded model handles it
before changing any code. Six tables use it as a column default. With that default gone from the DDL,
EF's convention for a Guid primary key is `ValueGeneratedOnAdd` backed by its own client-side
generator, which fills the key automatically and needs **no** change at the `Add(...)` sites.

Do **not** reflexively set `ValueGeneratedNever()` — that is what would force manual assignment
everywhere, and one missed site inserts `Guid.Empty`, where the *second* insert fails on a PK
collision. Check the scaffold output first; the likely correct outcome is "let EF generate it, confirm
it does" rather than an audit of every construction site.

**2. Foreign keys are OFF by default in SQLite.** Npgsql always enforced them; SQLite requires
`PRAGMA foreign_keys = ON` **per connection** (`Foreign Keys=True` in the Microsoft.Data.Sqlite
connection string). Without it every `FOREIGN KEY` in the DDL is decorative — and the
`ON DELETE CASCADE` on `RefreshTokens.UserId` silently stops cascading, so deleting a user leaks
token rows instead of erroring.

**3. `varchar(n)` lengths are not enforced.** SQLite ignores the length entirely. Anything relying on
the database to reject an over-long `Username` (256) or `Notes` (512) now needs app-layer validation.

**4. `timestamptz` has no native type.** Decide once and apply everywhere: **store UTC**. SQLite's
`CURRENT_TIMESTAMP` is already UTC, so it is a clean replacement for `now()`. Be deliberate about
`DateTime` vs `DateTimeOffset` in the scaffolded entities — mixing them is how timestamps drift.

**5. Enable WAL** (`PRAGMA journal_mode=WAL`). SQLite is single-writer, and game downloads hold long
read transactions. Without WAL a large download can block a metadata write.

### Smaller notes

- `double precision` → `REAL`, `boolean` → `INTEGER`, `date` → `TEXT`. EF handles all three.
- **Case-insensitive search degrades for non-ASCII, and there is no cheap fix.** EF turns `.ToLower()`
  into SQLite's `lower()`, which folds ASCII only — `Ø` will not match `ø`. `COLLATE NOCASE` has the
  **same** ASCII-only limitation, so it is not the answer despite looking like it. Real options are
  app-side normalization (normalize both the stored title and the query term) or the ICU extension.
  Given the library is mostly English game titles, accepting ASCII-only folding is defensible — just
  decide deliberately rather than assuming a collation fixed it.
- The scaffold command changes provider to `Microsoft.EntityFrameworkCore.Sqlite`. The
  post-scaffold rule still applies: **delete the generated `OnConfiguring` override.** Update CLAUDE.md.

### Work

- Write `Database/sqlite/01_schema.sql` from the Postgres DDL, applying the type mapping above.
- Swap the `EFModel` package reference to `Microsoft.EntityFrameworkCore.Sqlite`, re-scaffold, drop
  `OnConfiguring`.
- Fix the Guid generation sites.
- Set `Foreign Keys=True` and WAL on the connection.
- One-off data copy from the existing Postgres instance (small enough for a throwaway script).
- **Verify end-to-end before Phase 2** — the app is otherwise unchanged here, so any breakage is
  attributable to the database swap alone. That is the whole reason this phase goes first.

---

## Phase 2 — Single origin, and delete the auth plumbing

The API project serves the WASM bundle from `wwwroot` and hosts the endpoints. One process, one
origin, one port.

### Deletions

- `API/Startup/CorsConfig.cs` — gone entirely, along with the `UseCors()`-ordering invariant in
  `Program.cs` that broke login once.
- `GameTownApp/Helpers/TokenRefreshHandler.cs` and `CookieHandler.cs` — gone.
- The JWT parsing in `AuthService.cs:122-160` and its private `HttpClient` — gone.
- `GamesService.ResolveMedia` (`GamesService.cs:58-75`) — `/media/x.jpg` now just resolves.
- The compile-time API base URL in `GameTownApp/Program.cs:16` — relative paths instead. **This is
  what makes one published artifact run on every operator's LAN without a recompile.**

### Swap JWT for ASP.NET Core cookie auth

Same-origin means a normal `SameSite=Lax` cookie. The client no longer holds a token at all.

**This deletes the entire refresh-token mechanism.** Cookie auth with `SlidingExpiration = true`
does the job that `/auth/refresh`, the `refresh_token` cookie, `RefreshTokenValidationResult` and the
`RefreshTokens` table currently do between them. Drop the table in the Phase 1 schema rewrite rather
than migrating it.

Replace client-side claim parsing with a `GET /auth/me` endpoint returning username + roles; the
`AuthenticationStateProvider` calls that.

**Persist Data Protection keys to disk** (`PersistKeysToFileSystem`). Cookie auth encrypts the auth
cookie with them, and the default in-memory keyring means every restart silently logs everyone out —
which will look like a bug in the settings page, since saving settings restarts the app.

### Consequences

- `upload.js` drops its `token` parameter; the auth cookie rides along automatically. The pre-flight
  refresh in `UploadService.cs:31-37` goes away with it.
- `UseHttpsRedirection()` becomes conditional — see Phase 4.
- `Contracts` stays exactly as it is. It is still the wire contract, and still the boundary that must
  never gain an EF Core reference.
- Aspire's orchestration role largely disappears with only one runnable project. Keep it for the
  dashboard/telemetry or drop it — it stops being the entry point either way.

---

## Phase 3 — Runtime-editable config

With SQLite as primary there is **no bootstrap tier**: no connection string to configure, no setup
mode gated on reaching a database. The data directory is the only thing that must be known before the
app starts, and that comes from the install script as an env var or CLI arg.

Everything else lives in a `Settings` table and is editable live:

| Setting | Notes |
|---|---|
| `GameFilesPath` | Must exist and be writable; validate on save. |
| `RAWGApiKey` | Optional — the app should degrade to manual metadata entry without it. |
| `AllowedFileTypes` | **Net-new.** There is no upload allowlist today. |

### Re-hosted media must leave `wwwroot` — this is a data-loss bug for an appliance

`RAWGService.DownloadAndReplaceImageUrlsAsync` writes cover art and screenshots to
`Directory.GetCurrentDirectory()/wwwroot/media` (`RAWGService.cs:285`). That directory sits **inside
the published application**, so a self-contained `dotnet publish` over an existing install erases
every re-hosted image in the library. Harmless on a single instance that never upgrades in place;
unacceptable for something distributed with an upgrade path.

Media gets the same treatment as `GameFilesPath`: it moves into the data directory and is served with
a static-files mapping onto that path rather than from physical `wwwroot`. The `/media/{guid}.ext`
URLs stored in the database do not change, so no data migration is needed — only a file move and the
mapping. Note this touches the `UseStaticFiles()`-before-`UseAuthorization()` invariant in
`Program.cs`; the new mapping must keep that ordering or the anonymous library page stops rendering.

**Four things must live in the data directory and survive an upgrade:** the SQLite database, the Data
Protection keyring, uploaded game archives, and re-hosted media. Anything left in the app directory is
lost on the next install.

### The non-obvious refactor

`RAWGService` and `FileService` currently capture their key and path as **constructor arguments at DI
registration** (`DependenciesConfig.cs:41,53`) — read once at startup. Live-editable config means
moving both onto a settings source read per request. That is the real cost of the settings menu, and
it is more invasive than the UI itself.

Delete the three startup throws (`DependenciesConfig.cs:36,51,62`) as part of this.

### Allowed file types

Enforce **in the upload endpoint**, server-side. A check in the WASM client is advisory and trivially
bypassed. Note this pairs with accepted risk #2 in SECURITY-NOTES.md (no upload size limit) — a
distributed appliance with contributors the author has never met is a different threat model from a
personal LAN, and the size ceiling deserves revisiting at the same time.

### The settings GUI — a WASM page inside the SPA, not a Razor Page

An earlier draft put this on a server-rendered Razor Page. That was wrong, and the reason is worth
recording so it does not get "simplified" back: server rendering was chosen for the **first-run
wizard**, which has to work before the app is configured. The settings screen has no such constraint —
by definition the app is already running and configured when an admin opens it. Those two pages look
similar and have completely different requirements.

Shipping it as a Razor Page would put a visually foreign surface outside the app: `NavMenu.razor:48-57`
already has an `Administer` group gated on `AuthorizeView Roles="Admin"`, `UserManagement.razor`
establishes the admin-console pattern (tab strip, alert banners, `EditForm` + `DataAnnotationsValidator`),
and the whole app carries custom `gt-*` styling that a plain Razor Page would not pick up.

**So: `GameTownApp/Pages/Admin/Settings.razor`, `@attribute [Authorize(Roles = "Admin")]`, mirroring
the structure of `UserManagement.razor`** — tab strip, the same success/error banner pattern, `EditForm`
per section. Add a `Settings` link to the existing `Administer` nav group next to *Users & roles*.

Suggested tabs:

| Tab | Contents |
|---|---|
| **Storage** | `GameFilesPath`, media directory, free-space readout, a **Validate** button reporting exists / writable / space. |
| **Metadata** | `RAWGApiKey` with a **Test key** button that makes one live RAWG call and reports the result. Must state clearly that leaving it blank degrades to manual metadata entry rather than breaking the app. |
| **Uploads** | `AllowedFileTypes` as an add/remove chip list, plus the max upload size if the ceiling from accepted risk #2 gets reintroduced. |

### Endpoints the GUI needs

This is the cost acknowledged when we chose hosted WASM over Blazor Server — the work is real but
small and follows the existing `MapGroup` + thin-handler pattern. A new `SettingsEndpoints.cs`, all
`.RequireAuthorization("Admin")`:

- `GET /settings` — current values. **Never return secrets in cleartext**: send a masked
  `RAWGApiKey` (`"••••abcd"`) plus an `IsSet` flag, and treat an empty submitted value as "unchanged"
  rather than "clear it". Otherwise loading the page and pressing Save round-trips the key through the
  browser for no reason.
- `PATCH /settings` — validate then persist. Server-side validation is authoritative; the client's is
  for feedback only.
- `POST /settings/validate-path` — `Directory.Exists` + a real write-and-delete probe. Existence alone
  is not enough; the service user may lack write permission, and finding that out at first upload is a
  bad experience.
- `POST /settings/test-rawg-key` — one live RAWG call.

**Guard the path-validation endpoint.** It takes an arbitrary server path from a client and reports
whether it exists — that is a filesystem-probing primitive. `Admin`-only is the control; keep it
returning a plain boolean plus a fixed reason code, never raw exception text, which would leak
directory structure.

Corresponding client service: `GameTownApp/Services/SettingsService.cs`, matching `UserService.cs`
and returning the existing `ApiResult` type. Contracts go in `Contracts/Settings/`.

### Settings that need a restart

`GameFilesPath` and the media directory can change live — the services read them per request after the
refactor above. Anything Kestrel-level (bind address, port) cannot. Keep restart-requiring settings out
of this GUI entirely rather than showing a "restart required" banner: they belong to the install
script, and a settings page that can make the app unreachable is a support burden on someone else's LAN.

---

## Phase 4 — First run and distribution

### First-run wizard

A plain server-rendered Razor Page at `/setup`, reachable only while no admin user exists. It creates
the first admin account and collects the Phase 3 settings.

Server-rendered specifically because it runs before the app is configured — booting the whole SPA into
a broken-config state just to render a form is the thing we are avoiding.

**Remove the seeded `test` / `123456` admin from the seed script.** Accepted risk #3 in
SECURITY-NOTES.md is scoped to "a dev box"; shipping it to strangers turns it into a default
credential on every install. First-run account creation replaces it.

### TLS posture

This is where the original HTTPS problem resolves. Cross-origin `SameSite=None; Secure` *forced*
HTTPS; same-origin `SameSite=Lax` does not. So:

- **Default: HTTP on the LAN.** The Proxmox-script norm, and now technically sound rather than a
  compromise.
- **Documented opt-in:** operators with a domain put Caddy in front and get a real cert via ACME
  DNS-01 — no inbound reachability required. This is the answer for anyone exposing it more widely.
- Make `UseHttpsRedirection()` conditional so the default path does not redirect into a port that
  isn't listening.

Do not attempt to solve trusted TLS for arbitrary strangers' LANs. There is no clean answer, and a
private CA cannot be installed on a guest's phone.

### Install script

- Self-contained `dotnet publish` avoids making the runtime a prerequisite — worth the artifact size
  for a one-line installer.
- Create a data directory (SQLite file, Data Protection keys, uploaded archives), `chmod 600` on the
  database, dedicated service user.
- systemd unit.
- No credentials to generate — SQLite removed that whole class of install-time secret. Data Protection
  keys are generated by the framework on first boot; they just need a persistent, private directory.

---

## Phase 5 — Schema upgrades

There is no path today from an installed v1 to a v2 schema. Distribution makes that mandatory.

Numbered idempotent SQL scripts plus a `schema_version` table, applied at startup. This fits the
existing database-first model where the DDL is the source of truth — EF Core migrations would fight
the scaffolding workflow rather than help it.

---

## Not in scope

- Rewriting the catalogue UI. Phase 2 changes how pages fetch, not what they render.
- Multi-tenancy, remote access, or anything facing the public internet.
- The AngleSharp sanitiser CVE (accepted risk #1) — unchanged by any of this, still waiting on
  HtmlSanitizer 9.1.x stable. Distribution does raise its priority: a CSP is cheap insurance once
  strangers are running this.
