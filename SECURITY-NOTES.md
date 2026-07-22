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

### 3. The seeded development account

`Database/postgres/02_seed.sql` creates `test` / `123456` with the `Admin` role, and the password
hash is in the repo. Fine for a dev box; change or remove it before anyone else can reach the host.

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

Two orderings are load-bearing. Both have already caused outages once:

1. **`UseCors()` must come before `UseAuthentication()`/`UseAuthorization()`.** Browsers send no
   credentials on a CORS preflight, so with the fallback policy in place an `OPTIONS` request is
   denied by the authorization middleware and short-circuits *before* any
   `Access-Control-Allow-Origin` header is written. The browser then reports it as a CORS failure and
   never sends the real request. This broke login entirely.
2. **`UseStaticFiles()` must come before `UseAuthorization()`.** It is what keeps `/media` (cover art
   and screenshots) publicly readable so the anonymous library page renders.

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

- JWT in the response body; refresh token in an **HttpOnly, Secure, SameSite=None** cookie.
- `JwtSettings:Key`, the RAWG key and the connection string live in user-secrets, never in
  `appsettings.json` (which ships `SetInSecrets` placeholders and throws on startup if unset).
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
