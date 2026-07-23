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

## Installing and running

```bash
sudo ./install.sh                            # install or upgrade as a systemd service
```

Then open `http://<host>:5187/setup` to create the administrator. That page stops responding once one
exists. Re-running the installer upgrades in place: it backs up the database, replaces the
application, and leaves the data directory alone.

For development:

```bash
dotnet build GameTown.slnx                   # build everything (.slnx, not .sln)
dotnet run --project API                     # the whole app — SPA included
```

The **only** required configuration is the SQLite connection string; everything else is edited in the
app under *Administer → Settings*. The RAWG key is optional.

```bash
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Data Source=$HOME/gametown/gametown.db" --project API
```
[CLAUDE.md](CLAUDE.md) has the full setup, including the HTTPS dev-certificate trust step that Linux
needs before the browser will talk to the API.

There is currently **no test project** — the largest remaining gap. Each migration phase was verified
by hand against a running instance, and almost every bug found in the process produced a *working
build*: routes that returned a web page instead of JSON, a cookie the browser silently discarded, a
keyring that reset on restart, services still serving configuration captured at startup. None of it
would have been caught by compiling.

## Further reading

- [CLAUDE.md](CLAUDE.md) — conventions, setup, and the exact commands for re-scaffolding the model.
- [SECURITY-NOTES.md](SECURITY-NOTES.md) — accepted risks and the invariants not to break.
