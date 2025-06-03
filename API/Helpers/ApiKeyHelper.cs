using System.Security.Cryptography;
using System.Text;

namespace API.Helpers;

public static class ApiKeyHelper
{
    public static (string hash, string salt) HashPassword(string password)
    {
        byte[] saltBytes = RandomNumberGenerator.GetBytes(16);
        var pbkdf2 = new Rfc2898DeriveBytes(password, saltBytes, 100_000, HashAlgorithmName.SHA256);
        byte[] hashBytes = pbkdf2.GetBytes(32);
        return (Convert.ToHexString(hashBytes), Convert.ToHexString(saltBytes));
    }

    public static bool ValidatePassword(string providedPassword, string storedHash, string storedSalt)
    {
        byte[] saltBytes = Convert.FromHexString(storedSalt);
        var pbkdf2 = new Rfc2898DeriveBytes(providedPassword, saltBytes, 100_000, HashAlgorithmName.SHA256);
        byte[] hashBytes = pbkdf2.GetBytes(32);
        return Convert.ToHexString(hashBytes) == storedHash;
    }
}

