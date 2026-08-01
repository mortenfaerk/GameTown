using System.Security.Cryptography;
using System.Text;

namespace API.Helpers;

public static class ApiKeyHelper
{
    // PBKDF2 parameters. Changing any of these invalidates every stored password, so they are named
    // rather than repeated: the hash and the check must never be able to drift apart.
    private const int Iterations = 100_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private static readonly HashAlgorithmName Algorithm = HashAlgorithmName.SHA256;

    public static (string hash, string salt) HashPassword(string password)
    {
        byte[] saltBytes = RandomNumberGenerator.GetBytes(SaltBytes);
        return (Convert.ToHexString(Derive(password, saltBytes)), Convert.ToHexString(saltBytes));
    }

    public static bool ValidatePassword(string providedPassword, string storedHash, string storedSalt)
    {
        byte[] saltBytes = Convert.FromHexString(storedSalt);
        byte[] expected = Convert.FromHexString(storedHash);
        byte[] actual = Derive(providedPassword, saltBytes);

        // Constant-time, not `==` on the hex strings. String comparison returns as soon as two
        // characters differ, so how long a rejected login takes leaks how much of the digest was
        // guessed correctly. FixedTimeEquals also handles a stored hash of the wrong length without
        // a separate check.
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    // The static Pbkdf2 method rather than the Rfc2898DeriveBytes constructor, which is obsolete as
    // of SYSLIB0060. Byte-for-byte identical output for the same inputs, so passwords hashed by the
    // previous implementation still validate — this is a deprecation fix, not a rehash.
    private static byte[] Derive(string password, byte[] salt)
        => Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, Algorithm, HashBytes);
}

