using API.Helpers;
using API.Models.Auth;
using API.Services;
using EFModel.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;


namespace API.Endpoints;

public static class AuthEndpoints
{
    public static void AddAuthEndpoints(this WebApplication app)
    {
        // Necessarily anonymous — these are how you obtain a token in the first place. Must opt out
        // of the global fallback policy, or logging in would require already being logged in.
        var group = app.MapGroup("/auth")
           .AllowAnonymous()
           .WithTags("Authentication")
           .WithOpenApi()
           .WithDescription("Endpoints for API key authentication and JWT token issuance");

        group.MapPost("/login", Login).WithDescription("Get a JWT Token for use in other endpoints")
             .Accepts<LoginRequest>("application/json")
             .WithTags("Authentication")
             .WithName("Login")
             .Produces<TokenResponse>(StatusCodes.Status200OK)
             .Produces(StatusCodes.Status401Unauthorized)
             .Produces(StatusCodes.Status500InternalServerError)
             .Produces(StatusCodes.Status403Forbidden)
             .WithOpenApi();
        group.MapPost("/refresh", RefreshToken)
             .WithDescription("Refresh a JWT Token if the refresh_token cookie is still valid")
             .WithTags("Authentication")
             .WithName("RefreshToken")
             .Produces<TokenResponse>(StatusCodes.Status200OK)
             .Produces(StatusCodes.Status401Unauthorized)
             .WithOpenApi();
        group.MapPost("/logout", Logout)
             .WithDescription("Logs out the user and clears the refresh_token cookie")
             .WithTags("Authentication")
             .WithName("Logout")
             .Produces(StatusCodes.Status204NoContent)
             .WithOpenApi();
    }

    private static async Task<IResult> Login(HttpContext http, LoginRequest req, UserService userService)
    {
        if (http.Request.Cookies.TryGetValue("refresh_token", out var existingRefreshToken))
        {
            var refreshResult = await RefreshToken(http, userService);
            switch (refreshResult)
            {
                case Ok<TokenResponse> ok:
                    return ok;
            }
        }
        var matchedUser = await userService.AuthenticateUser(req);
        if (matchedUser == null)
        {
            return Results.Unauthorized();
        }
        var token = await userService.GetToken(matchedUser);
        if (token == null)
        {
            return Results.Problem("Kunne ikke generere token", statusCode: StatusCodes.Status500InternalServerError);
        }
       
        var refreshToken = await userService.GenerateAndStoreRefreshToken(matchedUser);
        var cookieOptions = new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.None,
            Expires = refreshToken.ExpiresAt
        };
        http.Response.Cookies.Append("refresh_token", refreshToken.Token, cookieOptions);
        
        return Results.Ok(token);
             
    }
    private static async Task<IResult> RefreshToken(HttpContext http, UserService userService)
    {
        if (!http.Request.Cookies.TryGetValue("refresh_token", out var refreshToken))
        {
            return Results.Unauthorized();
        }

        var refreshTokenValidationResult = await userService.ValidateRefreshToken(refreshToken);
        if (refreshTokenValidationResult.IsRefreshTokenInvalid)
        {
            return Results.Unauthorized();
        }else if (!refreshTokenValidationResult.IsSuccessful)
        {
            return Results.Problem("Kunne ikke validere refresh token", statusCode: StatusCodes.Status500InternalServerError);
        }
        if(refreshTokenValidationResult.RefreshToken == null || refreshTokenValidationResult.TokenResponse == null)
        {
            return Results.Problem("Kunne ikke generere token", statusCode: StatusCodes.Status500InternalServerError);
        }
        http.Response.Cookies.Append("refresh_token", refreshTokenValidationResult.RefreshToken.Token, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.None,
            Expires = refreshTokenValidationResult.RefreshToken.ExpiresAt
        });

        return Results.Ok(refreshTokenValidationResult.TokenResponse);
    }
    private static IResult Logout(HttpContext http)
    {
        http.Response.Cookies.Append("refresh_token", "", new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Expires = DateTimeOffset.UtcNow.AddDays(-1) // Expire immediately
        });
        return Results.NoContent();
    }
}
