using API.Endpoints;
using API.Startup;

var builder = WebApplication.CreateBuilder(args);

builder.AddDependencies();

var app = builder.Build();

app.UseOpenApi();

// Serves the Blazor WASM bundle from this project. Must precede UseStaticFiles so the
// _framework/ assets resolve, and both must precede UseAuthorization — see below.
app.UseBlazorFrameworkFiles();

app.UseStaticFiles();

app.UseHttpsRedirection();

// CORS must run BEFORE authentication/authorization. A browser sends no credentials on a preflight
// OPTIONS request, so with the authorization fallback policy in place the preflight gets denied and
// short-circuits before any Access-Control-Allow-Origin header is written — the browser then reports
// it as a CORS failure and never sends the real request. Login broke exactly this way.
app.ApplyCorsConfig();

app.UseAuthentication();
app.UseAuthorization();

app.AddRootEndpoints();
app.AddGamesTownGamesEndpoints();
app.AddMetaDataEndpoints();
app.AddUserEndpoints();
app.AddAuthEndpoints();
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
