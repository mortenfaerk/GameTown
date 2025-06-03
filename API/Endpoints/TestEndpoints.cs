using API.Models.Users;
using API.Services;
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
        group.MapPost("/add", AddUser).WithDescription("Adds a new user")
            .Accepts<UserCreationRequest>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .WithName("DEBUG: Add user")
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
    private static async Task<IResult> AddUser(UserCreationRequest userDTO, UserService _userService)
    {

        if (string.IsNullOrEmpty(userDTO.Username) || string.IsNullOrEmpty(userDTO.DisplayName))
        {
            return Results.BadRequest("User data is incomplete.");
        }
        var apiPlainsKey = await _userService.CreateUser(userDTO,"System");
        return Results.NoContent();
    }


}

