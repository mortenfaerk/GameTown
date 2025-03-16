using Scalar.AspNetCore;
using EFModel.Models;
using Microsoft.EntityFrameworkCore;
using API.Services;
using API.Endpoints;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddOpenApi();

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
var RAWGKey = builder.Configuration.GetValue<string>("RAWGApiKey") ?? throw new Exception("The app requires an API key for RAWG to be set!");

builder.Services.AddDbContext<DatabaseContext>(options =>
options.UseSqlServer(connectionString));
builder.Services.AddScoped<RAWGService>(serviceProvider =>
    new RAWGService(RAWGKey));
var app = builder.Build();

app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseHttpsRedirection();

app.MapGamesEndpoints();

app.Run();
