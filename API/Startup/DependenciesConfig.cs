using API.Services;
using EFModel.Models;
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
        builder.Services.AddScoped<RAWGService>(serviceProvider =>
            new RAWGService(RAWGKey));
    }
}
