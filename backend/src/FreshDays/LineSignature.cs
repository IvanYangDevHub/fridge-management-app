using System.Security.Cryptography;
using System.Text;

namespace FreshDays;

public static class LineSignature
{
    public static bool Verify(byte[] body, string? signature, string secret)
    {
        if (string.IsNullOrEmpty(signature) || string.IsNullOrEmpty(secret)) return false;
        try
        {
            var actual = Convert.FromBase64String(signature);
            var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException) { return false; }
    }
}
