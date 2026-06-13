using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using ThreeCommerce.Identity.Domain;

namespace ThreeCommerce.Identity.Infrastructure.Security;

/// <summary>
/// Argon2id via Konscious (vetted library — ADR-0012 prohibits hand-rolled crypto).
/// Parameters follow OWASP Password Storage Cheat Sheet first-choice guidance:
/// m=19456 KiB (19 MiB), t=2 iterations, p=1 lane. Encoded self-describing so
/// parameters can be raised later without invalidating existing hashes.
/// </summary>
public sealed class Argon2idPasswordHasher : IPasswordHasher
{
    private const int MemoryKib = 19_456;
    private const int Iterations = 2;
    private const int Parallelism = 1;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Compute(password, salt, MemoryKib, Iterations, Parallelism);
        return $"argon2id$m={MemoryKib},t={Iterations},p={Parallelism}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public bool Verify(string password, string encodedHash)
    {
        var parts = encodedHash.Split('$');
        if (parts.Length != 4 || parts[0] != "argon2id")
        {
            return false;
        }

        var parameters = parts[1].Split(',')
            .Select(p => p.Split('='))
            .ToDictionary(kv => kv[0], kv => int.Parse(kv[1]));

        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Compute(password, salt, parameters["m"], parameters["t"], parameters["p"]);

        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static byte[] Compute(string password, byte[] salt, int memoryKib, int iterations, int parallelism)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memoryKib,
            Iterations = iterations,
            DegreeOfParallelism = parallelism,
        };
        return argon2.GetBytes(HashBytes);
    }
}
