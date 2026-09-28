using System.Security.Cryptography;
using System.Text;
using Stempeluhr.Api.Models;

namespace Stempeluhr.Api.Services;

public static class TerminalAuthentication
{
    public static string? Authenticate(HttpRequest request, RuntimeSettings settings)
    {
        var id = request.Headers["X-Terminal-Id"].ToString();
        var authorization = request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.Ordinal)
            || !settings.TerminalTokens.TryGetValue(id, out var expected)
            || string.IsNullOrWhiteSpace(expected)) return null;

        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(authorization[7..]));
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(suppliedHash, expectedHash) ? id : null;
    }

    public static object[] Catalog(RuntimeSettings settings, IEmployeeService employees) =>
        settings.Employees.Where(e => e.IsEnabled && !string.IsNullOrWhiteSpace(e.ApiToken))
            .Select(e =>
            {
                var salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
                var verifier = string.IsNullOrWhiteSpace(e.Pin) ? null :
                    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{salt}:{e.Pin}"))).ToLowerInvariant();
                return (object)new { employee = employees.ToEmployeeDto(e),
                    cardId = NfcCardIdNormalizer.Normalize(e.NfcCardId), salt, verifier };
            }).ToArray();
}
