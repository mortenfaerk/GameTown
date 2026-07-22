using API.Endpoints;
using API.Startup;

var builder = WebApplication.CreateBuilder(args);

builder.AddDependencies();

var app = builder.Build();

app.UseOpenApi();

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

app.Run();
