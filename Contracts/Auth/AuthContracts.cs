using System.ComponentModel.DataAnnotations;

namespace GameTown.Contracts.Auth;

public class LoginRequest
{
    [Required(ErrorMessage = "Username is required.")]
    public string Username { get; set; } = string.Empty;

    [Required(ErrorMessage = "Password is required.")]
    public string Password { get; set; } = string.Empty;
}

/// <summary>
/// Who the caller currently is, as the server sees it.
///
/// This replaces the old TokenResponse. The client no longer holds a token to inspect — the identity
/// lives in an HttpOnly auth cookie it cannot read — so it asks the server instead, via GET /auth/me.
/// That is strictly better than the previous arrangement, where the browser parsed JWT claims itself
/// and therefore trusted a value it could also have tampered with.
/// </summary>
public class CurrentUser
{
    public string Username { get; set; } = string.Empty;
    public List<string> Roles { get; set; } = [];
}
