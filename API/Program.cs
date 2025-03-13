using Scalar.AspNetCore;
using EFModel.Models;
using Microsoft.EntityFrameworkCore;
using API.Services;
using Microsoft.AspNetCore.Identity;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddOpenApi();

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
var RAWGKey = builder.Configuration.GetValue<string>("RAWGApiKey");

builder.Services.AddDbContext<DatabaseContext>(options =>
options.UseSqlServer(connectionString));
builder.Services.AddScoped<RAWGService>(serviceProvider =>
    new RAWGService(serviceProvider.GetRequiredService<DatabaseContext>(), RAWGKey));
var app = builder.Build();

app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseHttpsRedirection();

app.MapGet("/games", async (DatabaseContext context) =>
{
    return await context.Games.ToListAsync();
});
app.MapPost("/games/{id}", async (DatabaseContext context, RAWGService rServ, int id) =>
{
    Game resultingGame = await rServ.GetGameById(id);
    context.Games.Add(resultingGame);
    await context.SaveChangesAsync();
}
);

app.Run();
