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

    public static bool VerifyPin(string pin, string? stored)
    {
        var parts = stored?.Split('$');
        if (parts is not [HashScheme, var iterationText, var saltHex, var hashHex]
            || !int.TryParse(iterationText, NumberStyles.None, CultureInfo.InvariantCulture, out var iterations)
            || iterations <= 0) return false;
        try
        {
            var expected = Convert.FromHexString(hashHex);
            var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), Convert.FromHexString(saltHex),
                iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
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
            return "Für das Wartungs-WLAN fehlt das Passwort.";
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

public sealed record TerminalServiceWifiDto(string Ssid, string Psk);

public sealed record TerminalMaintenanceDto(TerminalServiceWifiDto? ServiceWifi, string? MaintenancePinHash);
