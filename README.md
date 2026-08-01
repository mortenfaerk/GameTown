# GameTown

**Plex, but for games.** A private server for sharing freeware and abandonware game archives across a
home LAN: someone uploads a game once, everyone else browses a shelf of cover art and downloads it.

Installs from a single script onto an LXC container or VM, in the style of the Proxmox community
helper scripts. There is no database server to set up and no credentials to generate — the first
administrator is created in the browser on first visit.

Browsing and downloading are open to anyone who can reach the host — no account needed, because the
whole point is that a guest on the sofa can grab a game. Uploading and administering require an
account. Game metadata (cover art, screenshots, genres, developers) is pulled from the
[RAWG](https://rawg.io) games API and re-hosted locally, so the library keeps working when the
internet does not.

It is not built to face the public internet as it stands — see
[SECURITY-NOTES.md](SECURITY-NOTES.md).

---

## Install

On a Debian/Ubuntu-ish LXC container or VM, as root:

```bash
curl -fsSL https://raw.githubusercontent.com/mortenfaerk/GameTown/master/install.sh | bash
```

That resolves the latest release, verifies its checksum, unpacks it to `/opt/gametown` and starts it
as a systemd service listening on port 5187. The target needs neither a .NET runtime nor `sqlite3`:
the release is a self-contained build, and the application creates its own database from a schema
embedded in the binary.

Then open **`http://<host>:5187/setup`** and create the administrator. That page stops responding
once an admin exists, so do it before anyone else on the network can — until then it is an open
admin-creation form.

### What the container needs

- **x86_64.** The release is a self-contained `linux-x64` build; the installer refuses anything else
  rather than letting systemd report "Exec format error" three steps later.
- **`curl` and `tar`**, checked before anything is downloaded.
- **ICU.** A self-contained .NET build does *not* bundle it — it loads the system
  `libicuuc`/`libicui18n` at startup. Minimal container templates often leave it out, and the failure
  lands **after** a green install: the service dies immediately with *"Couldn't find a valid ICU
  package installed on the system"*, visible in `journalctl -u gametown`. The installer does not
  check for it. Install whatever runtime package your release ships — `apt install libicu-dev` pulls
  it in regardless of version, or find the exact name with `apt-cache search '^libicu[0-9]'`.
- **1 vCPU and 512 MB RAM is enough** — the running service sits around 180 MB resident, most of it
  reclaimable mapped assemblies. Give it 2 GB if you also intend to *build* in there
  (`GAMETOWN_SRC=`), since that needs the .NET SDK rather than just the app. First boot applies the
  schema and JITs on one core, so `systemctl start` can sit for tens of seconds before it reports
  ready.
- **Disk sized for the library.** The application is ~200 MB; everything else is the archives you
  upload. Note that an upload exists in two places at once: it buffers into the service's private
  `/tmp` — backed by the container's `/tmp`, normally the **root filesystem** — before landing in the
  archive directory. So pointing
  `GameFilesPath` at a big second disk does not spare the rootfs — size it for the largest archive
  you expect anyone to upload.

### Afterwards

```bash
journalctl -u gametown -f                  # logs
systemctl restart gametown                 # restart
```

Everything that matters lives in **`/var/lib/gametown`** — the database, uploaded archives, re-hosted
cover art, and the Data Protection keyring that keeps sign-ins valid across restarts. Back up that
directory and nothing else. Note that each upgrade leaves a `gametown.db.backup-<timestamp>` there
and never prunes them; delete old ones yourself.

Re-running the install command upgrades in place: it stops the service, backs up the database,
replaces the application directory and leaves the data directory untouched — and does nothing at all
if the latest version is already installed. Schema changes are applied by the application at startup.

Options go to `bash`, on the right-hand side of the pipe — putting them in front of `curl` sets them
for `curl` and not for the script:

```bash
curl -fsSL https://raw.githubusercontent.com/mortenfaerk/GameTown/master/install.sh \
    | GAMETOWN_PORT=8080 bash     # listen elsewhere

… | GAMETOWN_VERSION=v0.1.0 bash  # pin a version instead of taking the latest
… | GAMETOWN_FORCE=1 bash         # reinstall the version already installed
GAMETOWN_SRC=$PWD ./install.sh    # build from a checkout instead (needs the .NET SDK)
```

The service serves **plain HTTP** by default, which is the intended posture on a LAN: authentication
is a same-origin `SameSite=Lax` cookie, not one that requires `Secure`. It does mean passwords cross
the network in the clear — to terminate TLS, put a reverse proxy in front and add
`Environment=RequireHttps=true` to `/etc/systemd/system/gametown.service`.

### Behind a reverse proxy: uploads need three settings changed

GameTown sets no upload ceiling of its own by default, but a proxy in front of it has one and **the
proxy wins** — it answers the request before GameTown ever sees it. Every default is wrong for an
application whose main job is moving multi-gigabyte archives, and each fails in a way that looks like
a GameTown bug:

| Default | What the contributor sees |
|---|---|
| `client_max_body_size 1m` | *"That file is too large for the server to accept"* on anything over 1 MB |
| `proxy_request_buffering on` | The progress bar reaches 100%, then a long silence — it measured bytes reaching the proxy's disk, not GameTown |
| `proxy_read_timeout 60s` | *"The upload could not reach the server"* after a long upload that the server may well have completed |

For nginx, including **Nginx Proxy Manager** (Advanced tab of the proxy host):

```nginx
client_max_body_size 0;
proxy_request_buffering off;
proxy_read_timeout 3600s;
proxy_send_timeout 3600s;
client_body_timeout 3600s;
send_timeout 3600s;
```

Two things to watch:

- **Do not wrap these in a `location / { }` block.** Nginx Proxy Manager inserts the Advanced text
  inside the `server` block, and your own `location /` shadows the one it generated — taking
  `proxy_pass` with it, so the site stops working entirely. Bare directives inherit correctly.
- **`client_max_body_size 0` is deliberate.** It hands the size policy to GameTown, where
  *Administer → Settings → Uploads* can set a real ceiling that produces a useful message ("that file
  is over the 2000 MB limit") instead of an nginx error page.

Caddy needs only `request_body { max_size 0 }`; it has no equivalent response timeout by default.
Cloudflare's proxy enforces a **100 MB** upload cap on Free and Pro that no configuration removes —
the DNS record has to be grey-clouded, or uploads sent to a hostname that bypasses it.

### Storing the archives on a network share

The archive directory is the one thing people want on a NAS rather than in the container. Setting it
to `\\server\share` does not work and GameTown will tell you so: it runs as an unprivileged service
with `NoNewPrivileges=true`, so it cannot mount anything, whatever path you type. The share has to be
mounted first and GameTown pointed at the mountpoint.

`smb-mount.sh` does that, as root, on the machine GameTown is installed on. It is not part of the
release tarball — fetch it the same way as the installer:

```bash
curl -fsSLO https://raw.githubusercontent.com/mortenfaerk/GameTown/master/smb-mount.sh
chmod +x smb-mount.sh

./smb-mount.sh //nas/games                     # prompts for the username and password
./smb-mount.sh '\\nas\games' --user morten     # Windows-style address works too
```

It is deliberately not piped into `bash` like the installer: it prompts for a password, which a
piped-in script cannot do — its stdin is the script itself.

It writes three things: a root-owned `0600` credentials file under `/etc/gametown`, a systemd
`.mount` unit for the share, and a drop-in that makes `gametown.service` **require** that mount. The
password never reaches GameTown's database — the kernel's CIFS client is the only thing that reads
it. Needs `cifs-utils` (`apt install cifs-utils`).

That last drop-in is the part worth keeping. Without it, a NAS that goes away turns the mountpoint
back into an ordinary empty directory on the root filesystem, and GameTown keeps accepting uploads
into it — they disappear behind the share when it returns, and the container's disk fills up. With
it, the service simply does not start until the share is there.

Before it reports success the script mounts the share, confirms the filesystem really is `cifs`, and
writes and deletes a file **as the `gametown` user** — because a mount can succeed with credentials
that leave the service account unable to write, and finding that out at the first upload is too late.

GameTown checks the same thing from its side. Both `/setup` and *Administer → Settings* create the
directory if they can, then write and delete a probe file before saving, and report which filesystem
the directory actually sits on — so an unmounted mountpoint shows up as `ext4` where you expected
`cifs`, instead of quietly being the wrong disk.

---

## Architecture

.NET 10 throughout. **One process, one origin**: the API hosts the Blazor WebAssembly SPA from its own
`wwwroot` and serves the endpoints it calls. SQLite holds the catalogue; everything that must survive
an upgrade lives in a single data directory.

```
     browser
        │
        │  same origin — no CORS, no token, an HttpOnly SameSite=Lax cookie
        ▼
   ┌──────────────────────────────┐        ┌──────────────┐
   │  GameTown (single process)   │───────▶│  RAWG API    │
   │                              │        └──────────────┘
   │   Blazor WASM SPA (wwwroot)  │
   │   minimal-API endpoints      │
   │   /setup  first-run wizard   │
   └──────────────┬───────────────┘
                  │
                  ▼
      /var/lib/gametown          ← the only thing to back up
        gametown.db   catalogue, users, settings
        games/        uploaded archives
        media/        re-hosted cover art
        keys/         Data Protection keyring
```

Because the SPA resolves its API address at runtime rather than at compile time, one published
artifact runs on any LAN, port or reverse proxy without a rebuild.

### Projects

| Project | Type | Role |
|---|---|---|
| `API` | `Microsoft.NET.Sdk.Web` | Minimal-API backend: endpoints, services, auth, RAWG integration. |
| `GameTownApp` | Blazor WebAssembly | The SPA. Library, game detail, upload, admin console. |
| `Contracts` | classlib | Wire types shared by both ends. **No EF Core, no ASP.NET** — see below. |
| `EFModel` | classlib | EF Core `DbContext` and entities, **scaffolded from the live database**. |
| `Database` | SQL scripts | `sqlite/01_schema.sql` is the frozen baseline; `sqlite/migrations/` carries installs forward. |
| `Aspire.AppHost` | Aspire orchestrator | Runs the API and the SPA together for local development. |
| `Aspire.ServiceDefaults` | classlib | Shared OpenTelemetry, health checks, service discovery. |

### The Contracts project

Both the API and the SPA reference `Contracts`, so a change to a wire type breaks the build on both
sides instead of failing silently at runtime. It deliberately depends on nothing — in particular not
EF Core, since it is compiled into the WASM bundle.

That constraint shapes the design: contracts are plain classes with no entity-shaped constructors, and
entity→contract mapping lives in `API/Mapping/` (`GameMappings`, `UserMappings`). The client uses
`System.Net.Http.Json`, whose web defaults (camelCase, case-insensitive) line up with ASP.NET Core's
output, so there is no `[JsonPropertyName]` plumbing anywhere.

### API composition

`Program.cs` stays thin: `builder.AddDependencies()` wires everything up in
`API/Startup/DependenciesConfig.cs`, then endpoint groups register themselves through
`app.Add*Endpoints()` extension methods.

Endpoints (`API/Endpoints/*.cs`) are static classes that map a `MapGroup` and delegate to private
handlers. Handlers stay thin — validate input, translate exceptions like `KeyNotFoundException` into
`NotFound` — and push the work into scoped services (`API/Services/*.cs`).

> **Two things here are load-bearing.** The static-file middleware must precede `UseAuthorization()`,
> and `MapFallbackToFile` must stay `.AllowAnonymous()` — otherwise the authorization fallback policy
> puts the SPA shell itself behind login. Also: never put `.Accepts<T>()` on a GET or DELETE; it
> applies a content-type constraint that makes the route unmatchable, and under SPA-fallback hosting
> that returns a web page instead of a 404. All in [SECURITY-NOTES.md](SECURITY-NOTES.md).

### Authentication and authorization

Login signs in an **HttpOnly `gametown_auth` cookie** with sliding expiration; `/auth/logout` signs
out and `GET /auth/me` reports who you are. There is no token: the client cannot read the cookie, so
it asks the server rather than parsing claims it would otherwise have to trust. Two roles: `Admin`,
and `Contributor` (which `Admin` also satisfies).

Authorization is **secure by default** — a global fallback policy requires an authenticated user, so
an endpoint is protected unless it explicitly says `.AllowAnonymous()`. The intentionally public
surface is the library reads, downloads, `/auth` and the SPA shell.

The trade this makes: a cookie is attached by the browser, so **CSRF is a live threat class** where a
bearer token was structurally immune. `SameSite=Lax` is the mitigation, and it holds only because no
`GET` mutates state. That invariant, and the three cookie settings that fail silently if changed, are
in [SECURITY-NOTES.md](SECURITY-NOTES.md).

### Data layer — EFModel is generated

`EFModel/Models/` is scaffolded from a live SQLite database and carries `<auto-generated>` headers.
Changing the schema means adding a numbered migration under `Database/sqlite/migrations/` and
re-scaffolding — not hand-editing the entities, and not editing the frozen baseline. Full commands
and the post-scaffold cleanup step are in [CLAUDE.md](CLAUDE.md).

Because SQLite is dynamically typed, the **declared column type names are load-bearing**: the
scaffolder recovers `Guid`/`DateTime`/`bool` from names like `uniqueidentifier`, and a bare `TEXT`
column would silently become a `string`. That and three sibling traps — foreign keys off by default,
`ValueGeneratedNever` on Guid keys, GUID text casing — are documented in [CLAUDE.md](CLAUDE.md).
All four produce a working build and wrong behaviour.

GameTown's own entities use `Guid` primary keys; RAWG entities reuse RAWG's integer ids, which is why
games, developers, genres and screenshots are shared rows rather than per-game copies. That sharing is
the reason `RAWGService.EnsureRawgGamePersisted` resolves every related row against what is already
stored before attaching it.

### RAWG integration and media

`API/Services/RAWGService.cs` calls RAWG over REST and deserialises straight into the EF entities.
RAWG serves snake_case, so a `SnakeCaseNamingStrategy` contract resolver is applied — without it every
underscored field (including `background_image`) silently binds to null.

Cover art and screenshots are **downloaded and re-hosted** into the data directory's `media/` folder,
with the stored URLs rewritten to `/media/{guid}.ext` and served from there. They deliberately do not
live in `wwwroot`: an in-place upgrade replaces the application folder, which would silently delete
every cover in the library.

Uploaded archives are written to the configured archive directory under a generated GUID name; the
original filename is never used to build a path, and the extension must be on the allowlist configured
under *Administer → Settings*. Uploads go through
`GameTownApp/wwwroot/js/upload.js` rather than `HttpClient` — the browser's `fetch` reports no upload
progress, so an `XMLHttpRequest` is used to drive a real progress bar. It also keeps large archives
out of the WASM heap.

---

## Development

Building and running from a checkout — the installed appliance needs none of this:

```bash
dotnet build GameTown.slnx                   # build everything (.slnx, not .sln)
dotnet run --project API                     # the whole app — SPA included
dotnet run --project Aspire/Aspire.AppHost   # the same, under the Aspire dashboard
```

Do not launch `GameTownApp` on its own. The API serves the SPA's compiled bundle, so the WASM dev
server *looks* like it works and then resolves its API address to itself — every call comes back as
`index.html`.

The **only** required configuration is the SQLite connection string; everything else is edited in the
app under *Administer → Settings*. The RAWG key is optional.

```bash
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Data Source=$HOME/gametown/gametown.db" --project API
```
[CLAUDE.md](CLAUDE.md) has the full setup, including the HTTPS dev-certificate trust step that Linux
needs before the browser will talk to the API.

```bash
dotnet test Tests/GameTown.Tests/GameTown.Tests.csproj
```

88 tests, mostly HTTP-level against the real app on a throwaway database. They are aimed squarely at
the failure mode this codebase produces: almost every bug found while building it compiled and ran —
routes returning a web page instead of JSON, a cookie the browser silently discarded, a keyring that
reset on restart, services still serving configuration captured at startup. So the tests assert on
things like `Content-Type` and cookie flags, not just status codes.

They need the `sqlite3` CLI installed; the shipped application does not.

Some failures are out of reach of any unit test, because they live in the generated systemd unit
rather than in code — a connection string split at a space by `Environment=`, a readiness
notification never sent. `.github/workflows/install-test.yml` covers those by actually installing the
built artifact, twice, and checking the service comes up and an upgrade preserves the library.

Not covered: the SPA in a real browser.

### Releasing

`.github/workflows/release.yml` builds the self-contained tarball, publishes it with a `SHA256SUMS`
file and tags the repository on a push to `prod`. The version comes from `<Version>` in
`Directory.Build.props`, and the workflow refuses to run if that tag already exists — so forgetting to
bump it fails loudly instead of silently re-releasing. That release is what the install command above
downloads.

## Further reading

- [CLAUDE.md](CLAUDE.md) — conventions, setup, and the exact commands for re-scaffolding the model.
- [SECURITY-NOTES.md](SECURITY-NOTES.md) — accepted risks and the invariants not to break.
