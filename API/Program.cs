using API.Endpoints;
using API.Startup;

var builder = WebApplication.CreateBuilder(args);

builder.AddDependencies();

var app = builder.Build();

// Before anything serves a request. An install from an older build must be brought forward, and
// serving against a schema the code does not expect is worse than failing to start.
SchemaMigrator.ApplyMigrations(
    API.Startup.SqliteConnectionString.WithRequiredPragmas(
        builder.Configuration.GetConnectionString("DefaultConnection")),
    app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SchemaMigrator"));

app.UseOpenApi();

// Serves the Blazor WASM bundle from this project. Must precede UseStaticFiles so the
// _framework/ assets resolve, and both must precede UseAuthorization — see below.
app.UseBlazorFrameworkFiles();

app.UseStaticFiles();

// Re-hosted cover art and screenshots, served from the DATA directory rather than wwwroot.
//
// They used to be written into the published application's own wwwroot/media, which meant an
// in-place upgrade — replacing the app folder — silently deleted every cover in the library. The
// stored URLs are unchanged ("/media/{guid}.ext"); only the physical location moved.
//
// Like UseStaticFiles above, this must stay ahead of UseAuthorization: the library page is browsable
// anonymously, and the authorization fallback policy would otherwise put the artwork behind login.
app.UseMediaFiles();

// Opt-in, not automatic. The appliance ships serving plain HTTP on a LAN — which is sound now that
// authentication is a same-origin SameSite=Lax cookie rather than the SameSite=None; Secure cookie
// that forced HTTPS. Unconditional redirection here would bounce every request to a port nothing is
// listening on. Operators terminating TLS (Caddy + ACME DNS-01 is the documented route) set
// GameTown__RequireHttps=true.
if (app.Configuration.GetValue<bool>("RequireHttps"))
{
    app.UseHttpsRedirection();
}

// No CORS. The SPA and the API are the same origin now, so there is no preflight, no
// Access-Control-Allow-Origin, and no allowed-origins list to keep in step with wherever the app is
// deployed. The ordering hazard that came with it — UseCors had to precede the auth middleware or
// preflights were rejected by the fallback policy, which once broke login entirely — is gone too.
app.UseAuthentication();
app.UseAuthorization();

app.AddRootEndpoints();
app.AddGamesTownGamesEndpoints();
app.AddMetaDataEndpoints();
app.AddUserEndpoints();
app.AddAuthEndpoints();
app.AddSettingsEndpoints();

// The first-run wizard. Page routes are matched before MapFallbackToFile, so /setup reaches the Razor
// Page rather than being swallowed by the SPA shell. Access is gated inside the page on "does an
// admin already exist", evaluated per request, so it closes itself once setup is done.
app.MapRazorPages();
if (Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Development")
{
    app.AddTestEndpoints();
}

// Any request that matched no endpoint and no static file is a client-side route (/login,
// /addgame, a deep link into the library), so hand it the SPA shell and let the router sort it out.
//
// .AllowAnonymous() is load-bearing. The authorization FallbackPolicy in DependenciesConfig requires
// an authenticated user for anything that does not opt out, and this fallback is an endpoint like
// any other — without it index.html itself is behind auth, so a signed-out visitor cannot reach the
// page that would let them sign in. The library is meant to be browsable anonymously.
app.MapFallbackToFile("index.html").AllowAnonymous();

app.Run();

// Exposes the implicit Program class generated from these top-level statements so the test project
// can boot the real application through WebApplicationFactory<Program>. Without this it is internal
// and the tests would have to duplicate startup, which would test a different app than ships.
public partial class Program;
