# Security notes

Working notes for GameTown's security posture: what has been accepted knowingly, and the invariants
that are easy to break by accident. Read this before changing authorization, middleware order, file
handling, or the RAWG metadata path.

Threat model in one line: **a private server on a home LAN.** Browsing and downloading are open to
anyone who can reach the host; contributing and administering require an account. It is not intended
to face the public internet as it stands.

---

## Accepted risks

### 1. AngleSharp CVE-2026-54570 — a sanitiser bypass, in our sanitiser

**Shows up as `NU1902` on every build.** It is not noise, and it is not fixed by bumping AngleSharp.

`HtmlSanitizer` 9.0.967 (latest **stable**) hard-pins `AngleSharp [0.17.1]` — an exact-version
bracket, so the transitive dependency cannot be overridden. That AngleSharp version carries
**CVE-2026-54570**, an mXSS flaw whose stated impact is that *"HTML sanitizers trusting AngleSharp's
parsing could miss malicious content."* In other words, a bypass of the exact control we added it for.

Fixed in AngleSharp 1.5.0, currently reachable only through the `HtmlSanitizer` **9.1.x prerelease**
line (9.1.968-beta → AngleSharp 1.5.2). Running a beta as a security control was declined.

**Why it is survivable here:** half the CVE is unescaped `<`/`>` in serialised *attribute values*.
`API/Mapping/GameMappings.cs` therefore allows **no attributes at all** — `<a>` was dropped from the
tag allowlist specifically so the attribute allowlist could be empty, leaving nothing for that half
of the bug to act on. Tested with the CVE's own `<annotation-xml encoding="text/html">` shape:
stripped, while `<p>/<b>/<ul>/<li>` survived.

- **Upgrade trigger:** move to `HtmlSanitizer` 9.1.x as soon as it ships stable, then confirm
  `NU1902` is gone.
- **Do not** "fix" the warning by adding a direct `AngleSharp` reference — the hard pin means
  HtmlSanitizer would run against an API it was not compiled for.
- **Real class-level fix:** a CSP (see Not done, below).

### 2. No upload size limit

`API/Startup/DependenciesConfig.cs` sets Kestrel's `MaxRequestBodySize = null` and
`FormOptions.MultipartBodyLengthLimit = long.MaxValue`, deliberately.

Before this, uploads silently failed above **~28.6 MB** (the Kestrel default) with a 413, while the
UI advertised 2 GB. Removing the ceiling was a deliberate choice over picking a number.

Uploading requires the `Contributor` policy, so this is **not an anonymous vector** — but there is no
backstop against a contributor filling the disk. If GameTown ever grows untrusted contributors, put a
ceiling back.

There *is* now a file-type allowlist (`Administer -> Settings -> Uploads`), enforced in the upload
endpoint via `FileService.IsAllowedFileTypeAsync`. It must stay enforced server-side: the SPA also
filters the file picker, but that is a convenience, and anything can POST to `/GTGames/Add` directly.
The size ceiling remains the open half of this risk.

### 3. CSRF, reintroduced by moving to cookie authentication

Authentication used to be a JWT the client attached to each request by hand. A bearer token in a
header is structurally **immune** to CSRF: a cross-site request cannot make the browser add it. The
auth cookie that replaced it is attached by the *browser*, on every request to this origin, including
ones initiated by another site. That is a real regression and it was taken knowingly.

**The mitigation is `SameSite=Lax`** (`DependenciesConfig.cs`). Lax withholds the cookie from
cross-site subresource requests — including cross-site `POST` and `fetch` — which defeats the classic
attack.

**The invariant that makes it hold: a `GET` must never change state.** Lax *does* still send the
cookie on top-level cross-site GET navigation, so a state-changing GET would be directly forgeable by
a link. Every mutating route today is `POST`/`PATCH`/`DELETE`. Keep it that way; this is the single
easiest thing here to break by accident, and nothing in the build will complain.

Residual risk is a same-site attacker or a browser that does not honour SameSite. For a private LAN
appliance that is acceptable. **The upgrade path is antiforgery tokens** (`AddAntiforgery` plus a
header echoed by the SPA) and it becomes necessary if this is ever exposed beyond a LAN.

### 4. The seeded development account

`Database/sqlite/02_seed.sql` creates `test` / `123456` with the `Admin` role, and the password hash
is in the repo. Fine for a dev box; **shipping it in an installable build would put a known default
credential on every install**, which is a different risk entirely. Phase 4 of
SQLITE-APPLIANCE-PLAN.md replaces it with first-run admin creation.

---

## Invariants — things that look harmless to change and are not

### Authorization is secure-by-default

`DependenciesConfig.cs` sets an authorization `FallbackPolicy` requiring an authenticated user, so
**a new endpoint is protected unless it explicitly opts out**. This exists because three endpoints
were once missed and shipped anonymous — `PATCH /GTGames/update`, `DELETE /GTGames/{id}` and all of
`/meta`.

Two consequences worth holding onto:

- A fallback policy only proves *authentication*, never a role. Anything needing a role still has to
  say so: `.RequireAuthorization("Contributor")` / `("Admin")`.
- The intentionally public surface must stay explicitly `.AllowAnonymous()`:
  `/auth/*`, `GET /GTGames/{id}`, `GET /GTGames/download/{id}`, `GET /GTGames/getPaged/{page}/{pageSize}`,
  `GET /GTGames/search/`, and the Development-only OpenAPI/Scalar endpoints.

### Middleware order in `API/Program.cs`

1. **`UseBlazorFrameworkFiles()` and `UseStaticFiles()` must come before `UseAuthorization()`.** This
   is what keeps `/media` (cover art and screenshots) and the WASM bundle publicly readable, so the
   anonymous library page renders at all.
2. **`MapFallbackToFile("index.html")` must stay `.AllowAnonymous()`.** The fallback is an endpoint
   like any other, so the `FallbackPolicy` otherwise puts the SPA shell itself behind authentication
   — and a signed-out visitor cannot reach the page that would let them sign in.

> The old first entry here was that `UseCors()` had to precede the auth middleware, because a
> credential-less preflight was rejected by the fallback policy before any
> `Access-Control-Allow-Origin` header was written — which once broke login entirely. Same-origin
> hosting removed CORS and that whole hazard with it.

### Never put `.Accepts<T>(...)` on a GET or DELETE

`Accepts` describes a request **body**, and it applies a content-type constraint to the endpoint. A
GET or DELETE carries no body and no `Content-Type`, so the route becomes unmatchable by any normal
client — including this app's own `HttpClient`.

Four routes shipped this way (`GET /GTGames/getPaged`, `/GTGames/search`, `/GTGames/{id}`,
`/meta/*`, plus `GET /users/get` and `DELETE /users/delete`). While the SPA was a separate origin the
symptom was a 404. Since the API began serving the SPA it is worse: the request falls through to
`MapFallbackToFile` and returns **200 `text/html`**, so the caller sees success and parses the SPA
shell as JSON.

That is the general hazard of SPA-fallback hosting — a mis-declared API route no longer 404s, it
silently returns a web page. When an API call behaves strangely, check the response `Content-Type`
before anything else.

### The settings endpoints are more sensitive than they look

`POST /settings/check-path` takes an arbitrary server path from the client and reports whether it
exists and is writable — a filesystem-probing primitive. `POST /settings/test-rawg-key` makes the
server issue an outbound request. Both are `Admin`-only, and both must stay that way.

Neither returns exception text. `check-path` answers with a fixed reason code
(`ok`/`not-absolute`/`permission-denied`/`not-found`/`io-error`/`invalid`) precisely because raw
exception messages would disclose directory structure from an endpoint whose whole job is reporting
on server paths. The client translates those codes into prose.

`GET /settings` returns the RAWG key **masked** (last four characters) plus an `IsSet` flag, never the
value. A blank key on `PATCH` therefore means "unchanged", not "clear" — clearing is a separate
explicit flag. This is what keeps a secret from being round-tripped through the browser just so an
untouched form can post it back.

### File paths must never come from the client

`GameTownGamePatchRequest` has **no `Url` member**, and `GTGamesService.UpdateGame` does not assign
one. It used to. Combined with the then-unauthenticated update route, that gave an unauthenticated
**arbitrary file read** (poison the path, then `GET /GTGames/download/{id}`) and an **arbitrary file
delete**. The stored path is server-generated at upload and must stay that way.

Defence in depth: any stored path goes through **`FileService.TryResolveGameFile`**, which resolves it
and proves it sits inside `GameFilesPath` before anything opens or deletes it. Use it — do not act on
`game.Url` directly.

> Worth remembering *why* the delete case got dangerous: the old code targeted a directory that never
> matched real uploads, so the delete was a silent no-op. **Repairing that broken file operation is
> what turned it into a working arbitrary-delete primitive**, because the path was attacker-controlled.
> Fixing a broken file operation is a security change when the path comes from user input.

### RAWG HTML is untrusted

RAWG is community-editable, and its game descriptions are rendered with Blazor's `MarkupString`
(the `dangerouslySetInnerHTML` equivalent) on a **public** page. `GameMappings.ToContract` sanitises
`Description` on the **read** path — deliberately, so rows stored before sanitisation existed are
cleaned without a migration.

`MetaDataEndpoints.GetGame` must keep returning `game.ToContract()` and not the raw EF entity. It used
to return the entity, which bypassed the mapping layer entirely and would be a second, unsanitised
feed straight past the sanitiser.

### Auth mechanics

- No token anywhere. Identity is an **HttpOnly, SameSite=Lax `gametown_auth` cookie** with sliding
  expiration; `GET /auth/me` is how the SPA learns who it is. The JWT, the rotating refresh token and
  the `RefreshTokens` table are all gone.
- `SecurePolicy` is `SameAsRequest`, not `Always`, because the appliance default is plain HTTP on a
  LAN and `Always` would make the browser silently discard the cookie.
- The cookie handler's `OnRedirectToLogin`/`OnRedirectToAccessDenied` events are overridden to return
  **401/403 instead of a 302**. Left at the default, `fetch` follows the redirect and receives
  `200 text/html`, so a client sees success and parses a login page as JSON.
- **Data Protection keys are persisted to the data directory.** The default in-memory keyring would
  invalidate every auth cookie on restart, which presents as "saving a setting logged everyone out".
- The RAWG key and the connection string live in user-secrets, never in `appsettings.json` (which
  ships `SetInSecrets` placeholders and throws on startup if unset).
- After re-scaffolding EFModel, delete the generated `OnConfiguring` override — it hardcodes the
  connection string, password included, into source.

---

## Not done

- **No Content-Security-Policy.** This is the one change that would neutralise the whole XSS class
  regardless of sanitiser bugs, and it is the proper compensating control for accepted risk 1. It
  needs care with Blazor WASM's `wasm-unsafe-eval` requirement, and a wrong directive white-screens
  the app — so it wants its own pass with browser verification.
- **No automated tests.** There is no test project at all. Several of the issues above were found by
  hand after reaching production-shaped code; an HTTP-level suite covering the anonymous-vs-role
  matrix, a CORS preflight, and a media fetch at the URL the *browser* composes would have caught most
  of them.

## Re-running a review

`/security-review` reviews the pending diff on the current branch. The `security-guidance` plugin also
runs pattern checks on edits and reviews diffs at the end of a turn.
