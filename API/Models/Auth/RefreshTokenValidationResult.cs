using EFModel.Models;

namespace API.Models.Auth;

public class RefreshTokenValidationResult
{
    public bool IsSuccessful { get; init; }
    public bool IsRefreshTokenInvalid { get; init; }
    public TokenResponse? TokenResponse { get; init; }
    public RefreshToken? RefreshToken { get; init; }

    public static RefreshTokenValidationResult Success(TokenResponse token, RefreshToken refreshToken) => new()
    {
        IsSuccessful = true,
        TokenResponse = token,
        RefreshToken = refreshToken,

    };

    public static RefreshTokenValidationResult InvalidRefreshToken() => new()
    {
        IsSuccessful = false,
        IsRefreshTokenInvalid = true
    };

    public static RefreshTokenValidationResult ServerError() => new()
    {
        IsSuccessful = false,
        IsRefreshTokenInvalid = false
    };
}
