namespace Stempeluhr.Api.Services;

/// <summary>Womit die Sitzung des Stempels geöffnet wurde.</summary>
public enum StampMethod
{
    Pin,
    Card,
}

/// <summary>
/// Wie gestempelt wurde (issue #146), für Telegram-Meldung und API-Log.
/// <paramref name="TerminalId"/> stammt nur aus der geprüften
/// Terminal-Anmeldung, nie aus Client-JSON. <paramref name="Web"/> ist der
/// Hinweis der Seite /clock. Ohne Angaben bleibt die Meldung wie bisher.
/// </summary>
public sealed record StampOrigin(StampMethod? Method, string? TerminalId = null, bool Web = false)
{
    /// <summary>Terminal-IDs vergibt der Admin; lange IDs dürfen die Meldung nicht sprengen.</summary>
    private const int TerminalIdLimit = 40;

    /// <summary>
    /// Wertet den Hinweis des Clients aus: "web" für die Seite /clock, "card"
    /// bzw. "pin" für die Sitzung eines Offline-Stempels. Unbekannte Werte
    /// zählen nicht.
    /// </summary>
    public static StampOrigin Create(StampMethod? verified, string? terminalId, string? clientSource)
    {
        var source = clientSource?.Trim().ToLowerInvariant();
        var method = verified ?? source switch
        {
            "card" => StampMethod.Card,
            "pin" => StampMethod.Pin,
            _ => (StampMethod?)null,
        };
        return new StampOrigin(method, string.IsNullOrWhiteSpace(terminalId) ? null : terminalId.Trim(), source == "web");
    }

    /// <summary>Text für die Meldung, null = unbekannt (älterer Kiosk).</summary>
    public string? Describe()
    {
        if (TerminalId is { } id)
        {
            id = id.Length <= TerminalIdLimit ? id : id[..(TerminalIdLimit - 1)] + "…";
            return Method switch
            {
                StampMethod.Card => $"Karte am Terminal {id}",
                StampMethod.Pin => $"PIN am Terminal {id}",
                _ => $"Terminal {id}",
            };
        }

        if (Web)
        {
            return "Webbrowser";
        }

        // Karten gibt es nur am Terminal, auch ohne Terminal-Anmeldung.
        return Method == StampMethod.Card ? "Karte am Terminal" : null;
    }

    /// <summary>
    /// Für das API-Log: nie PIN oder Karten-ID, nur die Art. Anders als die
    /// Meldung nennt es auch die PIN ohne Terminal, wie das Attribut am Span.
    /// </summary>
    public override string ToString() =>
        Describe() ?? (Method == StampMethod.Pin ? "PIN ohne Terminal" : "unbekannt");
}
