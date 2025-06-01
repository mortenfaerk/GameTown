using API.Services;
using EFModel.Models;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;

namespace API.Startup;

public static class DependenciesConfig
{
    public static void AddDependencies(this WebApplicationBuilder builder)
    {
        builder.AddServiceDefaults();
        builder.Services.AddOpenApiServices();

        var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
        var RAWGKey = builder.Configuration.GetValue<string>("RAWGApiKey") ?? throw new Exception("The app requires an API key for RAWG to be set!");

        builder.Services.AddDbContext<DatabaseContext>(options =>
            options.UseSqlServer(connectionString)
            );
        builder.Services.AddScoped<RAWGService>(provider =>
        {
            var dbContext = provider.GetRequiredService<DatabaseContext>();
            var rawgKey = builder.Configuration.GetValue<string>("RAWGApiKey") ?? throw new Exception("The app requires an API key for RAWG to be set!");
            return new RAWGService(rawgKey, dbContext);
        });

        var GameFilesPath = builder.Configuration.GetValue<string>("GameFilesPath");
        if (string.IsNullOrWhiteSpace(GameFilesPath) || (!Path.Exists(GameFilesPath)))
        {
            throw new Exception("The app requires a GameFilesPath to be set and it must exist!");
        }
        builder.Services.AddScoped<FileService>(provider =>
        {
            return new FileService(GameFilesPath);
        });
        builder.Services.AddScoped<GTGamesService>();
    }
}
