using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Stempeluhr.Api.Models;

namespace Stempeluhr.Api.Services;

/// <summary>
/// Wartungs-WLAN und Wartungs-PIN der Terminals. Beides pflegt der Admin
/// zentral; die Terminals holen es mit ihrem Token und legen es lokal ab, damit
/// die WLAN-Einstellung am Display auch ohne Netz entsperrt werden kann.
/// </summary>
public static class TerminalMaintenance
{
    public const int PinMinLength = 6;
    public const int PinMaxLength = 12;

    // Der Pi prüft die PIN mit hashlib.pbkdf2_hmac; das Format muss dazu passen.
    private const string HashScheme = "pbkdf2-sha256";
    private const int HashIterations = 200_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public static string HashPin(string pin)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, HashIterations,
            HashAlgorithmName.SHA256, HashBytes);
        return string.Join('$', HashScheme, HashIterations.ToString(CultureInfo.InvariantCulture),
            Convert.ToHexString(salt).ToLowerInvariant(), Convert.ToHexString(hash).ToLowerInvariant());
    }

    // Gleiche Grenzen wie auf dem Pi (maintenance_access.PIN_HASH, MAX_ITERATIONS):
    // ein kaputter oder manipulierter Hash in settings.json darf weder werfen
    // noch einen Save minutenlang blockieren.
    private const int MaxIterations = 2_000_000;
    private static readonly System.Text.RegularExpressions.Regex StoredHash = new(
        @"^pbkdf2-sha256\$([1-9][0-9]{0,6})\$([0-9a-f]{16,128})\$([0-9a-f]{32,128})$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public static bool VerifyPin(string pin, string? stored)
    {
        var match = StoredHash.Match(stored ?? string.Empty);
        if (!match.Success
            || !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var iterations)
            || iterations > MaxIterations
            || match.Groups[2].Length % 2 != 0 || match.Groups[3].Length % 2 != 0) return false;
        var expected = Convert.FromHexString(match.Groups[3].Value);
        var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), Convert.FromHexString(match.Groups[2].Value),
            iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public static string? ValidatePin(string pin) =>
        pin.Length is >= PinMinLength and <= PinMaxLength && pin.All(char.IsAsciiDigit)
            ? null
            : $"Die Wartungs-PIN braucht {PinMinLength} bis {PinMaxLength} Ziffern.";

    /// <summary>Gleiche Grenzen wie NetworkManager für WPA2-Personal.</summary>
    public static string? ValidateServiceWifi(string? ssid, string? psk)
    {
        if (string.IsNullOrEmpty(ssid)) return null;
        if (Encoding.UTF8.GetByteCount(ssid) > 32 || ssid.Any(char.IsControl))
            return "Die SSID des Wartungs-WLANs darf höchstens 32 Byte lang sein.";
        if (string.IsNullOrEmpty(psk))
            return "Für das Wartungs-WLAN fehlt das Passwort (bei einer neuen SSID bitte neu eingeben).";
        var valid = psk.Length is >= 8 and <= 63 && psk.All(c => c is >= ' ' and <= '~')
            || psk.Length == 64 && psk.All(char.IsAsciiHexDigit);
        return valid ? null : "Das Passwort des Wartungs-WLANs braucht 8 bis 63 druckbare Zeichen.";
    }

    /// <summary>Antwort für <c>/api/kiosk/maintenance</c>; nur für authentifizierte Terminals.</summary>
    public static TerminalMaintenanceDto Config(RuntimeSettings settings) => new(
        string.IsNullOrEmpty(settings.ServiceWifiSsid) || string.IsNullOrEmpty(settings.ServiceWifiPsk)
            ? null
            : new TerminalServiceWifiDto(settings.ServiceWifiSsid, settings.ServiceWifiPsk),
        string.IsNullOrEmpty(settings.MaintenancePinHash) ? null : settings.MaintenancePinHash);
}

/// <summary>
/// Ergebnisse von <see cref="TerminalMaintenance.VerifyPin"/> pro Hash und PIN,
/// als Singleton per DI. PBKDF2 kostet pro Prüfung um 100 ms; so prüft jeder
/// Save alle Mitarbeiter-PINs gegen die Wartungs-PIN, rechnet aber nur neue
/// Paare - einmal nach dem Start bzw. nach einer neuen Wartungs-PIN (neues
/// Salt). Schlüssel ist ein SHA-256 von Hash und PIN: keine Klartext-PIN bleibt
/// im Speicher, auch nicht die gelöschter Mitarbeiter oder abgelehnter Saves.
/// </summary>
public sealed class PinMatchCache(Func<string, string, bool> verify)
{
    private const int MaxEntries = 10_000;

    private readonly ConcurrentDictionary<string, bool> results = new();

    public bool Matches(string pin, string stored)
    {
        if (results.Count >= MaxEntries) results.Clear();
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{stored}\n{pin}")));
        if (results.TryGetValue(key, out var known)) return known;
        var matches = verify(pin, stored);
        results[key] = matches;
        return matches;
    }
}

public sealed record TerminalServiceWifiDto(string Ssid, string Psk);

public sealed record TerminalMaintenanceDto(TerminalServiceWifiDto? ServiceWifi, string? MaintenancePinHash);
