namespace API.Models.Auth;

public class TokenResponse
{
    public required string Token { get; set; }
    public DateTime ExpiresUTC { get; set; }
}
