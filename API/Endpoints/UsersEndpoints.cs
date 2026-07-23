
using API.Helpers;
using API.Models.Users;
using API.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;


namespace API.Endpoints;

public static class UserEndpoints
{
    public static void AddUserEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/users")
           .WithTags("Users")
           .WithOpenApi()
           .WithDescription("Endpoints managing API users, and their keys");

        group.MapPost("/add", AddUser).WithDescription("Adds a new user")
            .RequireAuthorization("Admin")
            .Accepts<UserCreationRequest>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .WithName("Add user")
            .WithOpenApi();

        group.MapGet("/get", GetUser).WithDescription("Get user")
            .RequireAuthorization("Admin")
            .Produces<UserContract>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .WithName("Get user")
            .WithOpenApi();

        group.MapPut("/update", UpdateUser).WithDescription("Update user")
            .RequireAuthorization("Admin")
            .Accepts<UserUpdateRequest>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<string>(StatusCodes.Status400BadRequest)
            .Produces<string>(StatusCodes.Status404NotFound)
            .Produces<string>(StatusCodes.Status500InternalServerError)
            .WithName("Update user")
            .WithOpenApi();
        group.MapDelete("/delete", DeleteUser).WithDescription("Delete user")
            .RequireAuthorization("Admin")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<string>(StatusCodes.Status400BadRequest)
            .Produces<string>(StatusCodes.Status404NotFound)
            .Produces<string>(StatusCodes.Status500InternalServerError)
            .WithName("Delete user")
            .WithOpenApi();
        group.MapGet("/getAll", GetAllUsers).WithDescription("Get all users")
            .RequireAuthorization("Admin")
            .Produces<List<UserContract>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status500InternalServerError)
            .WithName("Get all users")
            .WithOpenApi();
        group.MapGet("/getAllRoles", GetAllRoles).WithDescription("Get all roles")
            .RequireAuthorization("Admin")
            .Produces<List<RoleContract>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status500InternalServerError)
            .WithName("Get all roles")
            .WithOpenApi();
        group.MapPost("/addUserToRole", AddUserToRole).WithDescription("Add user to role")
            .RequireAuthorization("Admin")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<string>(StatusCodes.Status400BadRequest)
            .Produces<string>(StatusCodes.Status404NotFound)
            .Produces<string>(StatusCodes.Status500InternalServerError)
            .WithName("Add user to role")
            .WithOpenApi();
        group.MapDelete("/removeUserFromRole", RemoveUserFromRole).WithDescription("Remove user from role")
            .RequireAuthorization("Admin")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<string>(StatusCodes.Status400BadRequest)
            .Produces<string>(StatusCodes.Status404NotFound)
            .Produces<string>(StatusCodes.Status500InternalServerError)
            .WithName("Remove user from role")
            .WithOpenApi();
        group.MapPost("/addRole", AddRole).WithDescription("Add role")
            .RequireAuthorization("Admin")
            .Accepts<RoleCreationRequest>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<string>(StatusCodes.Status400BadRequest)
            .Produces<string>(StatusCodes.Status500InternalServerError)
            .WithName("Add role")
            .WithOpenApi();
        group.MapPut("/updateRole", UpdateRole).WithDescription("Update role")
            .RequireAuthorization("Admin")
            .Accepts<RoleUpdateRequest>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<string>(StatusCodes.Status400BadRequest)
            .Produces<string>(StatusCodes.Status404NotFound)
            .Produces<string>(StatusCodes.Status500InternalServerError)
            .WithName("Update role")
            .WithOpenApi();
        group.MapDelete("/deleteRole", DeleteRole).WithDescription("Delete role")
            .RequireAuthorization("Admin")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<string>(StatusCodes.Status400BadRequest)
            .Produces<string>(StatusCodes.Status404NotFound)
            .Produces<string>(StatusCodes.Status500InternalServerError)
            .WithName("Delete role")
            .WithOpenApi();
    }


    private static async Task<IResult> AddUser(UserCreationRequest userDTO, UserService _userService, ClaimsPrincipal user)
    {
        if (user == null)
        {
            return Results.BadRequest("User data is null.");
        }
        if (string.IsNullOrEmpty(userDTO.Username) || string.IsNullOrEmpty(userDTO.DisplayName))
        {
            return Results.BadRequest("User data is incomplete.");
        }
        if (user.Identity == null || user.Identity.Name == null)
        {
            return Results.BadRequest("User identity is null.");
        }
        var userCreationResponse = await _userService.CreateUser(userDTO, user.Identity.Name);
        if(!userCreationResponse)
        {
            return Results.Problem("Failed to create user.", statusCode: 500);
        }
        return Results.NoContent();
    }
    private static async Task<IResult> GetUser(string userId, UserService _userService)
    {
        if (string.IsNullOrEmpty(userId))
            return Results.BadRequest("User ID is null or empty.");
        if (!Guid.TryParse(userId, out Guid parsedUserId))
            return Results.BadRequest("Invalid User ID format.");

        var user = await _userService.GetUserById(parsedUserId);

        if (user == null)
            return Results.NotFound("User not found.");

        return Results.Ok(user);
    }
    private static async Task<IResult> UpdateUser(UserUpdateRequest userDTO, UserService _userService, ClaimsPrincipal user)
    {
        if (user == null || user.Identity?.Name == null)
            return Results.BadRequest("User context missing");

        var result = await _userService.UpdateUser(userDTO, user.Identity.Name);

        if (result.UserNotFound)
            return Results.NotFound("User not found.");

        if (!result.Success)
            return Results.Problem(result.ErrorMessage ?? "Unknown error", statusCode: 500);

        return Results.NoContent();
    }
    private static async Task<IResult> DeleteUser(string userId, UserService _userService)
    {
        if (!Guid.TryParse(userId, out Guid parsedGuid))
            return Results.BadRequest("Invalid User ID format.");
        var result = await _userService.DeleteUser(parsedGuid);
        if (result.UserNotFound)
            return Results.NotFound("User not found.");
        if (!result.Success)
            return Results.Problem(result.ErrorMessage ?? "Unknown error", statusCode: 500);

        return Results.NoContent();

    }
    private static async Task<IResult> GetAllUsers(UserService _userService)
    {
        var users = await _userService.GetAllUsers();
        return Results.Ok(users);
    }
    private static async Task<IResult> GetAllRoles(UserService _userService)
    {
        try
        {
            return Results.Ok(await _userService.GetAllRoles());
        }
        catch (Exception ex)
        {
            // Log the exception here if needed
            return Results.Problem($"An error occurred while fetching roles.\n {ex} \n", statusCode: 500);
        }
    }
    private static async Task<IResult> AddUserToRole(string userId, string roleId, UserService _userService)
    {
        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(roleId))
            return Results.BadRequest("User ID or Role ID is null or empty.");
        if (!Guid.TryParse(userId, out Guid parsedUserId) || !Guid.TryParse(roleId, out Guid parsedRoleId))
            return Results.BadRequest("Invalid User ID or Role ID format.");
        var result = await _userService.AddUserToRole(parsedUserId, parsedRoleId);
        if (result.UserNotFound)
            return Results.NotFound("User not found.");
        if (result.RoleNotFound)
            return Results.NotFound("Role not found.");
        if (!result.Success)
            return Results.Problem(result.ErrorMessage ?? "Unknown error", statusCode: 500);
        if (result.ErrorMessage == "User already has this role.")
            return Results.Problem(result.ErrorMessage, statusCode: 400);
        return Results.NoContent();
    }
    private static async Task<IResult> RemoveUserFromRole(string userId, string roleId, UserService _userService)
    {
        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(roleId))
            return Results.BadRequest("User ID or Role ID is null or empty.");
        if (!Guid.TryParse(userId, out Guid parsedUserId) || !Guid.TryParse(roleId, out Guid parsedRoleId))
            return Results.BadRequest("Invalid User ID or Role ID format.");
        var result = await _userService.RemoveUserFromRole(parsedUserId, parsedRoleId);
        if (result.UserNotFound)
            return Results.NotFound("User not found.");
        if (result.RoleNotFound)
            return Results.NotFound("Role not found.");
        if (result.ErrorMessage == "User does not have this role.")
            return Results.Problem(result.ErrorMessage, statusCode: 400);
        if (!result.Success)
            return Results.Problem(result.ErrorMessage ?? "Unknown error", statusCode: 500);

        return Results.NoContent();
    }
    private static async Task<IResult> AddRole(RoleCreationRequest roleDTO, UserService _userService, ClaimsPrincipal user)
    {
        if (user == null || user.Identity?.Name == null)
            return Results.BadRequest("User context missing");
        if (string.IsNullOrEmpty(roleDTO.Name))
            return Results.BadRequest("Role name is null or empty.");
        try
        {
            await _userService.AddRole(roleDTO, user.Identity.Name);
            return Results.NoContent();
        }
        catch (Exception ex)
        {
            // log the exception here if needed
            return Results.Problem($"An error occurred while adding the role.\n{ex}\n", statusCode: 500);
        }

    }
    private static async Task<IResult> UpdateRole(RoleUpdateRequest roleUdpateDTO, UserService _userService, ClaimsPrincipal user)
    {
        if (user == null || user.Identity?.Name == null)
            return Results.BadRequest("User context missing");
        var result = await _userService.UpdateRole(roleUdpateDTO, user.Identity.Name);
        if (result.RoleNotFound)
            return Results.NotFound("Role not found.");
        if (!result.Success)
            return Results.Problem(result.ErrorMessage ?? "Unknown error", statusCode: 500);
        return Results.NoContent();
    }
    private static async Task<IResult> DeleteRole(string roleId, UserService _userService)
    {
        if (string.IsNullOrEmpty(roleId))
            return Results.BadRequest("Role ID is null or empty.");
        if (!Guid.TryParse(roleId, out Guid parsedRoleId))
            return Results.BadRequest("Invalid Role ID format.");
        var result = await _userService.DeleteRole(parsedRoleId);
        if (result.RoleNotFound)
            return Results.NotFound("Role not found.");
        if (result.RoleInUse)
            return Results.Problem(result.ErrorMessage ?? "Role is in use and cannot be deleted.", statusCode: 400);
        if (!result.Success)
            return Results.Problem(result.ErrorMessage ?? "Unknown error", statusCode: 500);
        return Results.NoContent();

    }
}
