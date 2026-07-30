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
| 2b — cookie auth | **Done**, verified (`fd2c230`) |
| 3 — config + settings GUI | **Done**, verified (`95a1730`) |
| 4 — first run + install | **Done**, verified (`a76a463`) |
| 5 — schema upgrades | **Done**, verified |

All phases are complete. The plan below is kept as the record of what was decided and why.

The lesson that held across all five phases: **the failure mode here is silence.** Almost every trap
found — foreign keys off, `ValueGeneratedNever` on Guid keys, scaffolder type inference, GUID text
casing, a cookie handler answering 401 with a 302, `SecurePolicy=Always` dropping the cookie over
HTTP, an unpersisted keyring, services reading config captured at startup, a form emitting no
antiforgery token — produced a **working build**. None of them would have been caught by compiling,
and several would have looked like a different bug entirely. Every one was found by running the thing
and asserting on the result.

The gap this section used to record — no test project, every phase verified by a throwaway harness
that never entered the repo — is closed. `Tests/GameTown.Tests` holds 88 passing tests built largely
from the "how you will know it works" sections below, and `.github/workflows/install-test.yml`
installs the built artifact on real systemd, twice, to cover what those tests cannot reach. What
remains untested is the SPA in a browser; see SECURITY-NOTES.md.

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

### 2a — hosting (DONE, `4e9250c`)

The API serves the WASM bundle; the client resolves its base address at runtime. `MapFallbackToFile`
is `.AllowAnonymous()` because the authorization `FallbackPolicy` treats it as an endpoint like any
other — without that, `index.html` sits behind auth and a signed-out visitor cannot reach the page
that would let them sign in.

### 2b — swap JWT for ASP.NET Core cookie auth

The client stops holding a token at all. Same-origin means an ordinary `SameSite=Lax` cookie, and
cookie auth with `SlidingExpiration = true` does the job that `/auth/refresh`, the `refresh_token`
cookie, `RefreshTokenValidationResult` and the `RefreshTokens` table currently do between them.

#### This is a real security-posture regression: CSRF comes back

**Write this up in SECURITY-NOTES.md, not just a code comment.** It is the one genuine regression in
the whole migration and the notes exist for exactly this kind of knowingly-accepted trade.

A bearer token is attached by *our* code, so a cross-site request never carries it — JWT-in-header is
structurally CSRF-immune. A cookie is attached by the *browser*, so it is not. The mitigation:

- `SameSite=Lax` withholds the cookie on cross-site subresource requests, including cross-site
  `POST`/`fetch`. That kills classic CSRF.
- Lax **does** send the cookie on top-level cross-site GET navigation. So the mitigation holds only
  because every state-changing route is `POST`/`PATCH`/`DELETE`. Record that as a standing
  invariant: **a GET must never mutate state.** It is currently true and easy to break by accident.
- Residual risk (same-site attacker, non-conforming browser) is acceptable for a LAN appliance.
  Antiforgery tokens are the documented upgrade path if this ever faces the internet.

Recommendation: accept with `SameSite=Lax`, documented — do not silently adopt it.

#### Server work

`API/Endpoints/AuthEndpoints.cs` shrinks a lot. `Login` currently tries a refresh first
(`AuthEndpoints.cs:54-62`), issues a JWT, then mints and stores a refresh token; it becomes
authenticate → build `ClaimsPrincipal` → `SignInAsync`. `Logout` becomes `SignOutAsync`.
`RefreshToken` is deleted outright.

`UserService.GetToken` (`UserService.cs:237`) already assembles exactly the claims we need — name,
identifier, one `ClaimTypes.Role` per role. Keep that claim-building and drop only the JWT encoding
around it. `GenerateAndStoreRefreshToken` and `ValidateRefreshToken` go.

Add `GET /auth/me` returning username + roles for the client's `AuthenticationStateProvider`.

**Four configuration traps, all of which produce a working build:**

1. **Cookie auth answers a 401 with a 302 to a login page.** That is the ASP.NET default and it is
   wrong for an API — `fetch` follows the redirect and gets `200 text/html`, so the client sees
   success and parses HTML as JSON. Override `Events.OnRedirectToLogin` to return 401.
2. **Do the same for `OnRedirectToAccessDenied` → 403**, or a role failure (`Contributor`/`Admin`)
   redirects to HTML instead of returning 403.
3. **`SecurePolicy` must be `SameAsRequest`, not the default `Always`.** The appliance default is
   HTTP on a LAN (Phase 4); `Always` marks the cookie `Secure` and the browser then silently drops
   it over HTTP, so login appears to succeed and every subsequent request is anonymous.
4. **Persist Data Protection keys to disk** (`PersistKeysToFileSystem` into the data directory). The
   default keyring is in-memory, so every restart invalidates every auth cookie. This will present
   as "saving a setting logs everyone out" in Phase 3, which reads as a settings bug, not a crypto
   one.

#### Dropping RefreshTokens is a cross-file cascade — land it as one commit

Removing the table means re-scaffolding, which deletes the `RefreshToken` entity, which breaks
everything referencing it. All of this has to move together or the tree will not compile:

- `Database/sqlite/01_schema.sql` — drop the table, re-scaffold `EFModel`.
- `EFModel/DatabaseContextConfiguration.cs` — remove the `Entity<RefreshToken>()` line. **This is the
  one that is easy to forget**, because it is hand-written and outside `Models/`.
- `API/Models/Auth/RefreshTokenValidationResult.cs` — delete.
- `UserService` — the two refresh methods, and the `GameTownUser.RefreshTokens` navigation goes away
  with the scaffold.

Grep for `RefreshToken` across the solution before calling 2b buildable.

#### Client work

- Rewrite `AuthService` around `GET /auth/me`; delete the JWT parsing (`AuthService.cs:122-160`) and
  its private `HttpClient` — the reason that second client existed (avoiding recursion through the
  refresh handler) disappears with the handler.
- Delete `TokenRefreshHandler.cs`. **Verify rather than assume** whether `CookieHandler.cs` can go —
  same-origin `fetch` should carry cookies by default, but confirm the WASM `HttpClient` credential
  default instead of trusting it.
- `GameTownApp/Program.cs` startup restore currently calls `RefreshTokenAsync()` inside a try/catch
  so an unreachable API cannot white-screen the app. Keep that guard exactly as it is; only the call
  changes, to `/auth/me`.
- `upload.js` drops its `token` parameter, and the pre-flight refresh in `UploadService.cs:31-37`
  goes with it — the cookie rides along on a same-origin XHR.
- One grep for anything else reading the JWT (Scalar's authorize affordance, `TestEndpoints`).

#### Then delete CORS

`API/Startup/CorsConfig.cs` and the `UseCors()` call, along with the ordering invariant in
`Program.cs` that broke login once. **Do this last** — deleting it before same-origin auth works
leaves a broken app whose cause is not obvious.

Also: `GamesService.ResolveMedia` (`GamesService.cs:58-75`) becomes unnecessary, `Contracts` stays
exactly as it is, and Aspire stops being the entry point with only one runnable project (keep it for
the dashboard or drop it).

#### How you will know it works

Boot with no cookie: `/auth/me` returns 401 and the app renders anonymous. Log in: the library still
lists, `Administer` appears in the nav, `/users/getAll` returns 200. Then **restart the process** and
reload — still signed in, which is what proves Data Protection keys persisted. Finally confirm a
role failure returns a real 403 and not a 302 to HTML.

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

### Storage and caching

A `Settings` table of key/value rows, read through a scoped `SettingsService`. Key/value rather than a
one-row-many-columns table because Phase 5 then adds a setting with an INSERT instead of an
`ALTER TABLE`.

Caching is the subtle part. Reading every setting from SQLite on every request is fine at this scale,
so **start with no cache** — it removes an entire class of "saved it, nothing changed" bug. If a cache
is added later it must be invalidated on save, and the invalidation has to be reachable from the
endpoint that writes: a scoped service holding a private dictionary silently gives every request its
own stale copy, which looks exactly like the save having failed.

Defaults belong in code, not in seed rows. An unset key should fall back to a documented default so a
half-populated table cannot leave the app in an unbootable state.

### How you will know it works

A probe that changes `RAWGApiKey` through `PATCH /settings`, then — **without restarting** — resolves
`RAWGService` and confirms it sees the new value. That single check is what proves the constructor-arg
refactor actually happened; every other part of this phase can look correct while the services quietly
keep serving their startup values.

Alongside that: saving a setting must not sign anyone out (proves Phase 2b's Data Protection work),
and `POST /settings/validate-path` must return 403 for a Contributor.

---

## Phase 4 — First run and distribution

### First-run wizard

A plain server-rendered Razor Page at `/setup`, reachable only while no admin user exists. It creates
the first admin account and collects the Phase 3 settings.

Server-rendered specifically because it runs before the app is configured — booting the whole SPA into
a broken-config state just to render a form is the thing we are avoiding.

Wiring notes:

- `AddRazorPages()` and `MapRazorPages()` have to be added; the API has never hosted Pages. Page
  routes match before `MapFallbackToFile`, so `/setup` will not be swallowed by the SPA fallback —
  but the fallback's `.AllowAnonymous()` must not be read as blanket permission: the setup page needs
  its own gate.
- **The gate is "no admin user exists", evaluated per request**, not a startup flag. A flag captured
  at boot stays open for the life of the process after the admin is created.
- Creating the first admin runs in a transaction with the username-uniqueness check inside it. Two
  concurrent setup submissions are a real race but a low-stakes one on a LAN; a transaction is
  enough, do not build a locking scheme for it.
- Once an admin exists `/setup` returns 404, not a redirect — a redirect confirms the endpoint exists
  and is worth probing.

**Remove the seeded `test` / `123456` admin from the seed script.** Accepted risk #3 in
SECURITY-NOTES.md is scoped to "a dev box"; shipping it to strangers turns it into a default
credential on every install. First-run account creation replaces it. Note the Phase 1 seed still
carries it and is marked accordingly — deleting it is part of *this* phase, and `02_seed.sql` then
seeds only the two roles.

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
- Create a data directory, dedicated service user, `chmod 600` on the database. **The four things
  that must live there and survive an upgrade: the SQLite file, the Data Protection keyring, uploaded
  archives, and re-hosted media.** Anything left in the app directory is destroyed by the next
  install — see the Phase 3 media move.
- systemd unit; the data directory path is passed as an env var, since it is the one thing that must
  be known before the app starts.
- No credentials to generate — SQLite removed that whole class of install-time secret. Data Protection
  keys are generated by the framework on first boot; they just need a persistent, private directory.
- The install script is also the *upgrade* script: it must detect an existing data directory and
  leave it alone. Test that path deliberately, because it is the one nobody runs until it matters.

### How you will know it works

Install into an empty directory: the app boots with no configuration and `/setup` is reachable.
Create the admin: `/setup` now 404s and login works. Then **run the installer again over the same
data directory** and confirm the library, uploads and media all survive — that is the check that
would have caught the `wwwroot/media` bug.

---

## Phase 5 — Schema upgrades

There is no path today from an installed v1 to a v2 schema. Distribution makes that mandatory: an
operator who installed last month must be able to take a new build without losing their library.

Numbered idempotent SQL scripts plus a `schema_version` table, applied at startup inside a
transaction. This fits the existing database-first model where the DDL is the source of truth — EF
Core migrations would fight the scaffolding workflow rather than help it.

Notes:

- **The SQLite version floor is the bundled `e_sqlite3`, not the host OS.** Because SQLitePCLRaw ships
  the native library, `ALTER TABLE ... DROP COLUMN` (3.35+) and modern upsert syntax are safe to rely
  on regardless of what the operator's machine has installed. This is the opposite of the usual
  "assume an ancient SQLite" caution and it makes migrations considerably less painful.
- `01_schema.sql` stays the source of truth for a *fresh* install; the numbered scripts carry an
  existing database forward. Both must end at the same schema — a mismatch between them is the
  classic failure here, and it only shows up on upgraded installs, never in development.
- Wrap each script in a transaction and record the version in the same transaction, so a failed
  upgrade leaves the database at the previous version rather than half-applied.
- Back up the database file before applying anything. It is one file — the cheapest possible safety
  net, and it turns a failed migration into a restore instead of a support thread.

### How you will know it works

Take a database created by an *older* schema, run the app, and confirm it upgrades and serves. Then
confirm a fresh install and an upgraded install produce the same `PRAGMA table_info` for every table
— that is the check that catches `01_schema.sql` and the numbered scripts drifting apart.

---

## Not in scope

- Rewriting the catalogue UI. Phase 2 changes how pages fetch, not what they render.
- Multi-tenancy, remote access, or anything facing the public internet.
- The AngleSharp sanitiser CVE (accepted risk #1) — unchanged by any of this, still waiting on
  HtmlSanitizer 9.1.x stable. Distribution does raise its priority: a CSP is cheap insurance once
  strangers are running this.
