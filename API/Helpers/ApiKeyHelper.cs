using System.Security.Cryptography;
using System.Text;

namespace API.Helpers;

public static class ApiKeyHelper
{
    public static (string hash, string salt) HashApiKey(string apiKey)
    {
        var salt = Guid.NewGuid().ToString();
        var combined = apiKey + salt;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(combined));
        return (Convert.ToHexStringLower(hash), salt);
    }

    public static bool ValidateApiKey(string providedApiKey, string storedHash, string storedSalt)
    {
        var combined = providedApiKey + storedSalt;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(combined));
        return Convert.ToHexStringLower(hash) == storedHash;
    }
}

