namespace Stempeluhr.Api.Services;

/// <summary>Was ein Knopf an einer Abwesenheitsantrags-Nachricht auslöst.</summary>
public enum TelegramAbsenceAction
{
    /// <summary>Erster Schritt: "Genehmigen" - fragt nur nach.</summary>
    AskApprove,

    /// <summary>Erster Schritt: "Ablehnen" - fragt nur nach.</summary>
    AskReject,

    /// <summary>Zweiter Schritt: "Ja, genehmigen" entscheidet.</summary>
    Approve,

    /// <summary>Zweiter Schritt: "Ja, ablehnen" entscheidet.</summary>
    Reject,

    /// <summary>"Zurück" zu den ersten beiden Knöpfen.</summary>
    Back,
}

/// <summary>
/// <c>callback_data</c> der Knöpfe: <c>a:&lt;aktion&gt;:&lt;id&gt;</c> mit der Antrags-ID
/// (Guid "N", 32 Zeichen). Eigenes Präfix, damit die Knöpfe der
/// Korrekturanträge (<see cref="TelegramCorrectionCallback"/>, <c>c:</c>)
/// unberührt bleiben. Telegram erlaubt höchstens 64 Bytes.
/// </summary>
public static class TelegramAbsenceCallback
{
    private const string Prefix = "a";

    private static readonly (TelegramAbsenceAction Action, string Code)[] Codes =
    [
        (TelegramAbsenceAction.AskApprove, "ask-approve"),
        (TelegramAbsenceAction.AskReject, "ask-reject"),
        (TelegramAbsenceAction.Approve, "approve"),
        (TelegramAbsenceAction.Reject, "reject"),
        (TelegramAbsenceAction.Back, "back"),
    ];

    public static string Format(TelegramAbsenceAction action, string id)
        => $"{Prefix}:{Codes.First(entry => entry.Action == action).Code}:{id}";

    public static bool TryParse(string? data, out TelegramAbsenceAction action, out string id)
    {
        action = default;
        id = string.Empty;
        var parts = data?.Split(':');
        if (parts is not [Prefix, var code, var requestId] || requestId.Length == 0)
        {
            return false;
        }

        foreach (var entry in Codes)
        {
            if (entry.Code == code)
            {
                action = entry.Action;
                id = requestId;
                return true;
            }
        }
        return false;
    }
}
