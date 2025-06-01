using System.Security.Claims;

namespace API.Endpoints;

public static class TestEndpoints
{
    public static void AddTestEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/test")
           .WithTags("Test")
           .WithOpenApi()
           .WithDescription("Endpoints Test during dev, only mounted if environment is set to dev!");

        group.MapGet("/secure-HelloWorld", GiveUserInfoBack)
            .RequireAuthorization("Admin")
            .WithTags("Test")
            .WithName("GetSecureData")
            .Produces(StatusCodes.Status200OK)
            .WithOpenApi();
    }
    private static async Task<IResult> GiveUserInfoBack(ClaimsPrincipal user)
    {
        await Task.Delay(20);

        var claims = user.Claims.Select(c => new
        {
            Type = c.Type,
            Value = c.Value
        });

        return Results.Ok(claims);
    }


}

