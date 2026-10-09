using API.Services;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Scalar.AspNetCore;

namespace API.Startup;

public static class OpenApiConfig
{
    public static void AddOpenApiServices(this IServiceCollection services)
    {
        services.AddOpenApi("v1", options => {
            options.AddDocumentTransformer<BearerSecuritySchemeTransformer>();
            options.AddDocumentTransformer((document, context, _) =>
            {
                document.Info = new()
                {
                    Title = "GameTown",
                    Version = "v1",
                    Description = """

                """,
                    Contact = new()
                    {
                        Name = "",
                        Email = "",
                        Url = new Uri("https://gametown.local/api")
                    }
                };
                return Task.CompletedTask;
            });
        });
    }

    /// <summary>
    /// Maps the API documentation — the Scalar UI under /scalar and the OpenAPI document under
    /// /openapi — and gates it on the ApiDocsEnabled setting.
    ///
    /// Mapped in EVERY environment now, where it used to be Development-only. Whether it ANSWERS is
    /// decided per request from the Settings table, because an admin toggles this from the settings
    /// page and a decision taken at startup could not follow the change. Same shape as the /setup
    /// wizard re-checking its own gate on every request rather than capturing it at boot.
    /// </summary>
    public static void UseOpenApi(this WebApplication app)
    {
        // A short-circuiting middleware rather than an `if` around the Map* calls below, and that is
        // load-bearing. Leaving the endpoints unmapped does NOT make them 404: an unmatched route
        // falls through to MapFallbackToFile and answers 200 text/html, so a caller asking for the
        // OpenAPI document gets the SPA shell and parses a web page as JSON. That is the exact
        // failure ApiRoutingTests exists to catch. Do not "simplify" this back to conditional
        // mapping.
        //
        // This runs before the endpoint executes, so a refused request never reaches the fallback.
        // (Not "before routing" — under minimal hosting WebApplication inserts UseRouting at the
        // head of the pipeline, so the route has already been matched by the time we get here. The
        // short-circuit is what matters, not the ordering.)
        app.Use(async (context, next) =>
        {
            // Path first, so an ordinary request does not pay for a database read. One prefix check
            // per family covers everything: Scalar serves its own 3.7MB JS bundle and loader from
            // under /scalar, and /scalar itself 302s to /scalar/.
            if (IsDocsPath(context.Request.Path))
            {
                // Resolved from RequestServices, never constructor-injected. This delegate is
                // effectively a singleton, while SettingsService is scoped over the request's
                // DbContext — capturing one here would hand every request the first request's
                // connection.
                var settings = context.RequestServices.GetRequiredService<SettingsService>();
                if (!await settings.GetApiDocsEnabledAsync())
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
            }

            await next();
        });

        // Both must opt out of the global authorization fallback policy or the docs UI 401s. They
        // stay anonymous on purpose: reachability is the setting's job, not authorization's, and a
        // signed-out visitor would otherwise get a bare 401 body — OnRedirectToLogin returns a
        // status rather than a redirect, so there is no login page to land on. The endpoints the
        // document DESCRIBES keep the authorization they already had; only their shapes are visible.
        app.MapOpenApi().AllowAnonymous();
        app.MapScalarApiReference(options =>
        {
            options.Title = "GameTown API";
            options.Theme = ScalarTheme.Saturn;
            options.Layout = ScalarLayout.Modern;
            options.HideClientButton = true;
        }).AllowAnonymous();
    }

    private static bool IsDocsPath(PathString path)
        => path.StartsWithSegments("/scalar") || path.StartsWithSegments("/openapi");
}

internal sealed class BearerSecuritySchemeTransformer(Microsoft.AspNetCore.Authentication.IAuthenticationSchemeProvider authenticationSchemeProvider) : IOpenApiDocumentTransformer
{
    public async Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        var authenticationSchemes = await authenticationSchemeProvider.GetAllSchemesAsync();
        if (authenticationSchemes.Any(authScheme => authScheme.Name == "Bearer"))
        {
            var requirements = new Dictionary<string, IOpenApiSecurityScheme>
            {
                ["Bearer"] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    In = ParameterLocation.Header,
                    BearerFormat = "Json Web Token"
                }
            };
            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes = requirements;

            // Operations is nullable on a path item; a path with none is not an error, just nothing
            // to attach a security requirement to.
            foreach (var operation in document.Paths.Values.SelectMany(path => path.Operations ?? []))
            {
                operation.Value.Security ??= [];
                operation.Value.Security.Add(new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference("Bearer", document)] = []
                });
            }
        }
    }
}