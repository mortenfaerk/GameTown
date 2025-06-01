using API.Endpoints;
using API.Startup;

var builder = WebApplication.CreateBuilder(args);

builder.AddDependencies();

var app = builder.Build();

app.UseOpenApi();
app.UseStaticFiles();
app.UseHttpsRedirection();


app.AddRootEndpoints();
app.AddGamesTownGamesEndpoints();
app.AddMetaDataEndpoints();

app.Run();
