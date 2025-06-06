using API.Endpoints;
using API.Startup;

var builder = WebApplication.CreateBuilder(args);

builder.AddDependencies();

var app = builder.Build();

app.UseOpenApi();

app.UseStaticFiles();

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

app.ApplyCorsConfig();
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
