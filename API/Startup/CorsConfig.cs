namespace API.Startup;

public static class CorsConfig
{
    private const string CorsPolicyName = "AllowAllOrigins";
    public static void AddCorsServices(this IServiceCollection services)
    {
        services.AddCors(options =>
        {
            options.AddPolicy("AllowAllOrigins",
                builder => builder
                .AllowCredentials()
                     .WithOrigins("https://localhost:7225","https://gametown.local:7225")
                    .AllowAnyMethod()
                    .AllowAnyHeader());
        });
    }
    public static void ApplyCorsConfig(this IApplicationBuilder app)
    {
        app.UseCors(CorsPolicyName);
    }
}
