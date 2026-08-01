using API.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Security.Claims;

namespace API.Endpoints;

public static class AuthEndpoints
{
    public static void AddAuthEndpoints(this WebApplication app)
    {
        // Necessarily anonymous — this is how you sign in in the first place. Must opt out of the
        // global fallback policy, or logging in would require already being logged in.
        //
        // /me is in the same group and also anonymous: it is the question "am I signed in?", and it
        // has to be answerable by someone who is not.
        var group = app.MapGroup("/auth")
           .AllowAnonymous()
           .WithTags("Authentication")
           .WithDescription("Sign-in, sign-out, and the current user's identity");

        group.MapPost("/login", Login)
             .Accepts<LoginRequest>("application/json")
             .Produces<CurrentUser>(StatusCodes.Status200OK)
             .Produces(StatusCodes.Status401Unauthorized)
             .WithName("Login")
             .WithDescription("Signs in and issues the auth cookie");
        // The (Delegate) cast is not decoration. Logout's signature — Task<IResult>(HttpContext) —
        // is implicitly convertible to RequestDelegate, so overload resolution prefers
        // MapPost(string, RequestDelegate), which returns IEndpointConventionBuilder and has no
        // .Produces(). The cast forces the MapPost(string, Delegate) overload and a
        // RouteHandlerBuilder. Login and Me avoid this by accident: Login takes extra parameters and
        // Me returns a bare IResult, so neither matches RequestDelegate.
        group.MapPost("/logout", (Delegate)Logout)
             .Produces(StatusCodes.Status204NoContent)
             .WithName("Logout")
             .WithDescription("Signs out and clears the auth cookie");
        group.MapGet("/me", Me)
             .Produces<CurrentUser>(StatusCodes.Status200OK)
             .Produces(StatusCodes.Status401Unauthorized)
             .WithName("CurrentUser")
             .WithDescription("Returns the signed-in user, or 401 when anonymous");
    }

    private static async Task<IResult> Login(HttpContext http, LoginRequest req, UserService userService)
    {
        var matchedUser = await userService.AuthenticateUser(req);
        if (matchedUser == null)
        {
            return Results.Unauthorized();
        }

        var principal = UserService.BuildPrincipal(matchedUser);

        // IsPersistent keeps the session across a browser restart, which is what people expect from
        // a media box on their own network. Sliding expiration (configured on the scheme) then
        // renews it as they use it — that pairing is what replaced the rotating refresh token.
        await http.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties { IsPersistent = true });

        return Results.Ok(ToCurrentUser(principal));
    }

    private static async Task<IResult> Logout(HttpContext http)
    {
        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.NoContent();
    }

    private static IResult Me(HttpContext http)
    {
        if (http.User.Identity?.IsAuthenticated != true)
        {
            return Results.Unauthorized();
        }
        return Results.Ok(ToCurrentUser(http.User));
    }

    private static CurrentUser ToCurrentUser(ClaimsPrincipal principal) => new()
    {
        Username = principal.Identity?.Name ?? string.Empty,
        Roles = principal.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList(),
    };
}
