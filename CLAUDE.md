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
| `Database` | plain SQL scripts (not an MSBuild project) | Source of truth for the schema: `postgres/01_schema.sql` + `postgres/02_seed.sql`. `Tables/` and `PostDeploymentScripts/` are the retired SQL Server DDL, kept for historical reference only. |
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

There is **no test project** in this solution.

## Required configuration (user secrets)

`API/appsettings.json` ships with `"SetInSecrets"` placeholders. The API **throws on startup** if these are missing. Set them via user secrets (`UserSecretsId` is already configured in `API.csproj`):

- `ConnectionStrings:DefaultConnection` — PostgreSQL (Npgsql) connection string
- `JwtSettings:Key`, `:Issuer`, `:Audience`, `:ExpiresInMinutes`, `:RefreshExpiresInDays`
- `RAWGApiKey` — API key for RAWG
- `GameFilesPath` — an **existing** directory for uploaded game archives (startup validates the path exists)

```powershell
dotnet user-secrets set "RAWGApiKey" "<key>" --project API
```

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

### Auth flow (JWT + refresh cookie)
- Login (`/auth/login`) validates credentials, returns a JWT in the body, and sets an **HttpOnly `refresh_token` cookie** (`SameSite=None`, `Secure`). `/auth/refresh` rotates the token from that cookie; `/auth/logout` clears it.
- Authorization policies are defined in `DependenciesConfig`: `Admin` requires role `Admin`; `Contributor` accepts `Contributor` **or** `Admin`. Protect endpoints with `.RequireAuthorization("Contributor")`.
- Frontend auth is in `GameTownApp/Services/AuthService.cs` (an `AuthenticationStateProvider` that parses JWT claims client-side). The app's `HttpClient` is wrapped in two delegating handlers: `TokenRefreshHandler` (refreshes the JWT before expiry, redirects to `/login` on failure, attaches the Bearer header) → `CookieHandler` (sets `BrowserRequestCredentials.Include` so the refresh cookie is sent). Note `AuthService` maintains its *own* internal `HttpClient` for the auth calls, separate from the DI-registered one used by feature services.

### Data layer — EFModel is generated, do not hand-edit
- `EFModel/Models/` (including `DatabaseContext.cs` and the entity classes) is **auto-generated** from the live PostgreSQL database (config in `EFModel/efpt.config.json`; files carry an `<auto-generated>` header). Changing the schema means editing `Database/postgres/*.sql`, applying it to the DB, and re-scaffolding — not editing these files by hand:

  ```bash
  dotnet ef dbcontext scaffold "<npgsql-connstring>" Npgsql.EntityFrameworkCore.PostgreSQL \
      -o Models -c DatabaseContext -f --project EFModel --startup-project EFModel
  ```

  **After every re-scaffold, delete the generated `OnConfiguring` override in `DatabaseContext.cs`** — it hardcodes the full connection string (password included) into source. The connection comes from user-secrets via `AddDbContext` in `DependenciesConfig.cs`.
- Table/column names use RAWG's snake_case (`HasColumnName("background_image")` etc.); C# properties are PascalCase. Several many-to-many joins (users↔roles, games↔developers/genres/screenshots) are configured as implicit join entities in `OnModelCreating`.
- Primary keys are `Guid`/`uuid` for GameTown entities (server default `gen_random_uuid()`); RAWG entities reuse RAWG's own `int` ids (`ValueGeneratedNever`).
- Table and column identifiers are **double-quoted in the Postgres DDL** to preserve the original mixed casing (`"GameTownGame"`, `"PasswordHash"`). Keep new DDL quoted the same way, or the scaffolded model will drift.

### RAWG integration & media
- `RAWGService` calls the RAWG REST API (via RestSharp + Newtonsoft.Json), paginates screenshots, and `AddGameToDb` upserts a RAWG game plus screenshots into the local DB.
- Screenshot images are **downloaded and re-hosted locally**: `DownloadAndReplaceImageUrlsAsync` writes them to `API/wwwroot/media/` and rewrites URLs to `/media/{guid}.ext` served as static files.

## Conventions
- Target framework is `net10.0` with nullable reference types and implicit usings enabled across all projects.
- GameTown game IDs are GUIDs passed as strings over the wire and parsed with `Guid.TryParse` in handlers (returning `400` on failure).
- Some user-facing error strings are in Danish (e.g. `"Kunne ikke generere token"`).
