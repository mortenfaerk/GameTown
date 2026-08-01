# Security notes

Working notes for GameTown's security posture: what has been accepted knowingly, and the invariants
that are easy to break by accident. Read this before changing authorization, middleware order, file
handling, or the RAWG metadata path.

Threat model in one line: **a private server on a home LAN.** Browsing and downloading are open to
anyone who can reach the host; contributing and administering require an account. It is not intended
to face the public internet as it stands.

Since these notes were first written GameTown became something other people install: a self-contained
release, a `curl … | bash` installer, a systemd unit and a first-run wizard. That moved several risks
from "my box" to "every box"; accepted risks 5–8 are the ones that only exist because of it.

Box art then added the first capability that points *outward*: the server now fetches a URL a
contributor typed. Being on the inside of a LAN is what makes that worth its own entry — see risk 9.

---

## Accepted risks

Entries marked *closed* are no longer live. They are kept, numbered in place, because the reasoning
is what stops the same decision being made again — and because other files point at these numbers.

### 1. AngleSharp CVE-2026-54570 — a sanitiser bypass, in our sanitiser — *closed*

`HtmlSanitizer` 9.0.967 hard-pinned `AngleSharp [0.17.1]` — an exact-version bracket, so the
transitive dependency could not be overridden. That AngleSharp carries **CVE-2026-54570**, an mXSS
flaw whose stated impact is that *"HTML sanitizers trusting AngleSharp's parsing could miss malicious
content."* In other words, a bypass of the exact control it was added for. It showed up as `NU1902`
on every build, and for a while the only fix was a prerelease, which was declined as a security
control.

**Closed by moving to `HtmlSanitizer` 9.1.974**, which is stable and depends on `AngleSharp 1.6.0`
— past the 1.5.0 fix. `NU1902` is gone from the build.

What was the compensating control stays in place: `API/Mapping/GameMappings.cs` allows **no
attributes at all** (`<a>` was dropped from the tag allowlist specifically so the attribute allowlist
could be empty), because half the CVE was unescaped `<`/`>` in serialised *attribute values*. A
description needs no attributes, so there is nothing to gain by relaxing it now — it is defence
against the next parser bug rather than a workaround for the last one.

`Tests/GameTown.Tests/SanitizerTests.cs` now pins all of it: script-bearing markup and the
advisory's own `<annotation-xml encoding="text/html">` shape stripped, formatting tags kept, every
attribute dropped.

- **Do not** pin `HtmlSanitizer` back below 9.1.x, and do not "fix" a future `NU1902` by adding a
  direct `AngleSharp` reference — a hard pin means HtmlSanitizer runs against an API it was not
  compiled for.
- Nothing watches for the next advisory: Dependabot security updates are **disabled** on the
  repository, so `NU1902` on a local build is the only signal.
- **Real class-level fix for this whole category:** a CSP (see Not done, below).

### 2. Upload size limit — now available, off by default

`API/Startup/DependenciesConfig.cs` sets Kestrel's global `MaxRequestBodySize = null` and
`FormOptions.MultipartBodyLengthLimit = long.MaxValue`, deliberately. Before that, uploads silently
failed above **~28.6 MB** (the Kestrel default) with a 413 while the UI advertised 2 GB.

There is now a real ceiling available: **`Administer → Settings → Uploads → Maximum upload size`**,
stored as `MaxUploadSizeMb`. **It defaults to 0, meaning unlimited** — an upgrade must not start
rejecting archives an install has been accepting — so the risk below is open until an operator sets
it. Enforcement is in two places, both in the upload path:

- `AddGameWithFile` lowers the per-request limit through `IHttpMaxRequestBodySizeFeature` before
  reading the body, so Kestrel can abort at the transport level.
- `ArchiveUpload.CopyAsync` counts the bytes it actually receives. This is the one that matters:
  `Content-Length` is written by the client and can understate the body.

Uploading requires the `Contributor` policy, so this is **not an anonymous vector**. With the ceiling
at 0 there is still no backstop against a contributor filling the disk.

There is also a file-type allowlist (same settings tab), enforced via
`FileService.IsAllowedFileTypeAsync` *before the first byte is written*. It must stay enforced
server-side: the SPA also filters the file picker, but that is a convenience, and anything can POST
to `/GTGames/Add` directly.

**Only one disk is exposed now.** Uploads used to be bound as an `IFormFile`, which spooled anything
over 64 KB to `Path.GetTempPath()` — under the unit's `PrivateTmp=true`, potentially a RAM-backed
tmpfs — and then copied it again into the archive directory, so a contributor could exhaust either.
`ArchiveUpload` streams the body straight to its final location: no temp copy, one disk, and half the
I/O. Do not reintroduce `[FromForm]` binding on this endpoint.

**A reverse proxy in front of GameTown has its own body limit and it takes precedence**, because it
rejects before the request arrives. That is a hardening opportunity as much as a footgun: an operator
who wants a hard cap that does not depend on application code can set one there. See the reverse
proxy section in README.md.

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

### 4. The seeded development account — *closed*

`Database/sqlite/02_seed.sql` used to create `test` / `123456` with the `Admin` role, password hash
committed. Defensible while GameTown was one instance on one box; shipping it in an installable build
would have put a **known default credential on every install**, which is a different risk entirely.

It is gone. The seed now creates the two roles and nothing else, and the first administrator is
created through the wizard at `/setup` (risk 5). Kept here because the seed file points back at this
entry, and because "add a convenient dev account to the seed" is exactly the sort of change that
looks harmless in a checkout and ships a backdoor.

### 5. `/setup` creates an administrator without authentication

On a fresh install `GET|POST /setup` is `[AllowAnonymous]` and will make whoever reaches it first an
`Admin`. It has to be: the authorization fallback policy demands an authenticated user, and there is
nobody to authenticate as yet.

The gate is "does any user hold the Admin role", evaluated **per request** and checked on `POST` as
well as `GET` (`API/Pages/Setup.cshtml.cs`). A boot-time flag would have stayed open for the life of
the process; this closes permanently the moment the first admin exists, and the page 404s from then
on.

**The window is real** and it is the sharpest thing about this appliance's posture: between
`systemctl start` and the operator's first visit, anyone who can reach the port owns the install.
Install and complete `/setup` before the host is reachable by anyone you would not hand Admin to. The
README says so at the point where it matters.

The form is a Razor Page `POST`, so antiforgery applies — and `API/Pages/_ViewImports.cshtml` is what
makes the tag helpers emit the token. Delete that file and every submission fails with a bare 400.

**Order matters inside the POST handler.** The archive path is probed *before* the administrator is
created, because this page 404s the moment one exists: a path rejected after the account was made
would leave the operator signed in, unable to store uploads, and with no wizard left to correct it.
`SetupPathTests` pins that ordering.

### 6. Installing means piping a script from GitHub into root's shell

`curl -fsSL …/install.sh | bash`, as root, is how this ships. It is the Proxmox-helper-script
convention and it was chosen knowingly; it is also a total-trust operation.

`install.sh` verifies `SHA256SUMS` **before** unpacking, and refuses to install on a mismatch. Be
precise about what that buys: the checksum file arrives from the same host over the same TLS
connection as the tarball, so it defends against a truncated or mangled download, not against a
compromised release or a compromised account. Anyone who can publish a release can publish a matching
`SHA256SUMS`.

The unit the installer writes is hardened in the obvious ways — `NoNewPrivileges`, `PrivateTmp`,
`ProtectSystem=full`, `ProtectHome`, and `ReadWritePaths` limited to the data directory — and the
service runs as a `--system` user with `nologin`. The data directory is `0700`, the database `0600`
and the keyring directory `0700`. That is not a completed hardening review: `ProtectSystem=strict`,
`PrivateDevices`, `ProtectKernelTunables` and `RestrictAddressFamilies` all apply here and none of
them are set.

Note that the per-upgrade `gametown.db.backup-<timestamp>` copies inherit root's umask rather than
`0600`; they are protected by the `0700` directory around them, so do not move them somewhere more
permissive.

### 7. SMB credentials live outside the application, deliberately

Storing the share's username and password in the `Settings` table would have been the obvious feature,
and it was rejected. A GameTown-shaped appliance protects that table with a `0700` data directory and
nothing else — no encryption at rest that is not decryptable by the same process — while a NAS
credential usually reaches far beyond the one share it was issued for.

`smb-mount.sh` (run as root, separately from the app) instead writes the password to a root-owned
`0600` file under `/etc/gametown` that only the kernel's CIFS client reads, and mounts the share with
`uid`/`gid` set to the service account. GameTown itself never sees the credential and never mounts
anything — it runs with `NoNewPrivileges=true` and could not mount if it wanted to.

Two details in that script are load-bearing:

- The credentials file is written **under `umask 077`**, not `chmod`-ed afterwards. A chmod after the
  fact leaves a window in which the whole point of the file is world-readable.
- `--password` on the command line is **refused**, because argv is readable through `/proc` for the
  lifetime of the process. It prompts, or takes `--password-file`.

It also writes a `RequiresMountsFor=` drop-in so the service will not start with the share missing.
That is not hardening for its own sake: without it an absent NAS turns the mountpoint back into an
ordinary local directory that GameTown will happily fill with uploads nobody can see afterwards.

### 8. Plain HTTP is the default

The unit sets `ASPNETCORE_URLS=http://0.0.0.0:<port>` and `UseHttpsRedirection` is opt-in behind
`RequireHttps` (`API/Program.cs`). This is sound in the sense that the auth cookie is same-origin and
`SameSite=Lax` rather than the `SameSite=None; Secure` one it replaced — nothing *requires* TLS to
function.

What it costs: the setup password, every login and every session cookie cross the LAN in cleartext,
readable by anything on the same segment. Accepted for a home network; the documented route out is a
reverse proxy terminating TLS plus `Environment=RequireHttps=true` in the unit.

**The key is root-level `RequireHttps`, with no prefix.** `install.sh` writes it correctly, but the
comment above the check in `API/Program.cs` says `GameTown__RequireHttps=true`, which binds to
`GameTown:RequireHttps` and is therefore never read. Setting it that way fails open — the operator
believes redirection is on and the app keeps serving plain HTTP, with no error either way. Nothing
asserts on this.

### 9. The server fetches URLs a contributor chose (box art)

Box art added a capability GameTown did not have: **a signed-in contributor can name a URL and have
the server fetch it.** That is server-side request forgery, and the position it happens from is the
interesting part — GameTown is installed *inside* a home LAN, so a request issued from it reaches
router admin pages, NAS interfaces, printers and (on a VPS) the cloud metadata endpoint at
`169.254.169.254`, none of which the attacker could reach directly.

Contained by `API/Services/ImageFetcher.cs`, which is the **only** path anything is allowed to
download an image through. Five controls, none of them defence in depth — each closes a different
hole, and removing any one restores a working attack:

- **http/https only.** `file://` would be an arbitrary file read.
- **Redirects are not followed** (`AllowAutoRedirect = false`). Every other check runs against the
  URL supplied; a followed 302 is checked against nothing, so this is what stops a public hostname
  redirecting to `169.254.169.254`.
- **Public addresses only, checked after DNS resolution** — a hostname is not an address, so
  `localhost` and a public name pointed at `192.168.x.x` are only refusable once DNS has answered.
  The vetted address is then **connected to directly** rather than re-resolved, which closes the
  DNS-rebinding window between check and use.
- **A byte ceiling enforced while reading** (10 MB). `Content-Length` is a claim, not a fact.
- **Magic-byte sniffing, with the stored extension taken from the sniff** — never from the URL, the
  upload's filename or the server's `Content-Type`. These files are written into the media directory
  and served straight back as static content **from the API's own origin**, so a stored `.svg` or
  `.html` would be stored XSS with the reach of the login page. SVG is excluded for exactly this
  reason and its absence is deliberate: it is an image to a person and a script host to a browser.

Two consequences worth knowing:

- **`RAWGService.RehostImageAsync` now goes through the same fetcher**, and must stay that way. RAWG
  is community-editable, so its image URLs are attacker-influenced too — by a longer route, not a
  weaker one. It previously used a per-call `new HttpClient()`, followed redirects, read an unbounded
  body and took the extension from the URL.
- **Failures are reported as fixed reason codes, never exception text.** The caller chose the
  destination, so the message can carry DNS state, proxy names and internal addresses back to them —
  the same rule `TestRawgKey` already followed.

`Tests/GameTown.Tests/BoxArtTests.cs` pins the refusals, including that nothing is written to the
media directory on a rejected request.

**Residual risk, accepted:** the address allowlist is IP-shaped, so a genuinely public host that an
attacker controls can still be fetched — this bounds *where* the server will connect, not *what* it
will retrieve. It also confirms reachability of any public address, which is a weak port-scan
primitive. Both need a Contributor account first.

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
  `GET /GTGames/search/`, the `MapFallbackToFile` SPA shell, the `/setup` page (risk 5) and the
  Development-only OpenAPI/Scalar endpoints. `Tests/GameTown.Tests/AuthorizationTests.cs` covers a
  sample of it — the paged list, search, `/auth/me` and the shell — not the whole list, so adding a
  route here is not the same as having it tested.

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
(`ok`/`not-absolute`/`unc-not-supported`/`permission-denied`/`not-found`/`io-error`/`invalid`)
precisely because raw exception messages would disclose directory structure from an endpoint whose
whole job is reporting on server paths. The client translates those codes into prose, and
`DirectoryProbe.Reasons` is the published set that `DirectoryProbeTests` holds it to.

The probe **writes and deletes a file**, and it **creates the directory** if it is missing — so it is
a filesystem-mutating primitive reachable by an Admin, and by the anonymous setup wizard before the
first admin exists (risk 5). It is bounded by running as the service account, which cannot write
anywhere interesting, and the probe file is removed in the same call. One implementation serves both
callers on purpose: a second one would drift, and the wizard would start accepting paths the settings
page rejects.

`DirectoryProbe` also reports the **filesystem name** of the mount the directory sits on. That is
operational rather than sensitive — it is the only way an operator can see that the network share
they configured is not actually mounted, which otherwise presents as uploads silently filling the
container's root disk under a directory the share hides when it returns.

`GET /settings` returns the RAWG key **masked** (last four characters) plus an `IsSet` flag, never the
value. A blank key on `PATCH` therefore means "unchanged", not "clear" — clearing is a separate
explicit flag. This is what keeps a secret from being round-tripped through the browser just so an
untouched form can post it back.

### File paths must never come from the client

`GameTownGamePatchRequest` has **no `Url` member**, and `GTGamesService.UpdateGame` does not assign
one. It used to. Combined with the then-unauthenticated update route, that gave an unauthenticated
**arbitrary file read** (poison the path, then `GET /GTGames/download/{id}`) and an **arbitrary file
delete**. The stored path is server-generated at upload and must stay that way.

Defence in depth: any stored path goes through **`FileService.TryResolveGameFileAsync`**, which
resolves it and proves it sits inside the configured archive directory before anything opens or
deletes it. Use it — do not act on `game.Url` directly. The containment check itself
(`TryResolveWithin`) is static and directly tested by `FileContainmentTests`, which lives in
`Tests/GameTown.Tests/SchemaTests.cs`.

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

### Nothing served from `/media` may be a script host

`/media` is static content on the API's **own origin**, which is the same origin as the login page and
the session cookie. Anything stored there that a browser will execute is stored XSS with full reach.

Two rules keep that true, and both live in `ImageFetcher.SniffExtension`:

- The stored extension comes from the file's **magic bytes**, never from the URL, the upload's
  filename or the `Content-Type` header — the extension is what decides the served `Content-Type`.
- The allowlist is **JPEG, PNG and WebP only**. SVG is excluded because it carries script; the
  omission is a decision, not an oversight, and `BoxArtTests` pins it so "why not SVG, it *is* an
  image" cannot quietly undo it.

Adding a format here means asking whether a browser will execute it, not whether it is an image.

### Tag names reach the query, but never as SQL

`TagService.Slugify` normalises a hand-typed name before it is stored or matched. That is a
correctness measure rather than a security one — every query goes through EF Core with parameters, so
the input is never concatenated into SQL — but it is worth knowing that the slug in `?tags=` is
user-supplied text on an anonymous route. It is compared, never interpolated. Keep it that way.

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
  On the appliance that directory is `/var/lib/gametown/keys`, mode `0700` — it decrypts every auth
  cookie, so treat it as being as sensitive as the database.
- Passwords are PBKDF2-HMAC-SHA256, 100 000 iterations, 32-byte derived key over a 16-byte random
  salt per user (`API/Helpers/ApiKeyHelper.cs`). Both the hash and the salt live in
  `GameTownUser`; there is no pepper.
- **The connection string is the only secret outside the database now.** It comes from user-secrets
  in development and from `Environment=` in the systemd unit on the appliance;
  `appsettings.json` ships a `SetInSecrets` placeholder. It is also the *only* configuration that
  throws at startup when missing — everything else, the RAWG key included, lives in the `Settings`
  table, because an unconfigured install has to boot far enough to serve its own setup page.
- After re-scaffolding EFModel, delete the generated `OnConfiguring` override — it hardcodes the
  connection string into source.

---

## Not done

- **No Content-Security-Policy.** This is the one change that would neutralise the whole XSS class
  regardless of sanitiser bugs, and it is the proper compensating control for accepted risk 1. It
  needs care with Blazor WASM's `wasm-unsafe-eval` requirement, and a wrong directive white-screens
  the app — so it wants its own pass with browser verification.
- **No rate limiting or lockout on `POST /auth/login`.** There is no `AddRateLimiter` anywhere, so
  password guessing is bounded only by PBKDF2's 100 000 iterations per attempt — a real per-attempt
  cost, and the reason this is survivable on a LAN rather than merely overlooked. It is the first
  thing to add if this ever becomes reachable from outside one.
- **Browser-level testing.** `Tests/GameTown.Tests` now covers the HTTP surface — the
  anonymous/Contributor/Admin matrix, the cookie's flags, and the content-type guard — but nothing
  exercises the SPA in a real browser. The Blazor `AuthenticationStateProvider`, the upload progress
  bar and media rendering are unverified above the wire.
- **The installer does not preflight ICU.** The release is self-contained but still loads the system
  `libicuuc`/`libicui18n` (no `InvariantGlobalization`, and no `libicu*` in the tarball). On a
  minimal container template the install succeeds and the service then dies on every start. Not a
  security issue — listed here because it is the same class of appliance-only failure as the rest of
  this section, and one `ldconfig -p | grep -q libicuuc` in `install.sh` would end it.

## Re-running a review

`/security-review` reviews the pending diff on the current branch. The `security-guidance` plugin also
runs pattern checks on edits and reviews diffs at the end of a turn.
