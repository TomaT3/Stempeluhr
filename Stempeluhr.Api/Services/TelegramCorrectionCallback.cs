namespace Stempeluhr.Api.Services;

/// <summary>Was ein Knopf an einer Korrekturantrags-Nachricht auslöst.</summary>
public enum TelegramCorrectionAction
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
/// <c>callback_data</c> der Knöpfe: <c>c:&lt;aktion&gt;:&lt;id&gt;</c> mit der Antrags-ID
/// (Guid "N", 32 Zeichen). Telegram erlaubt höchstens 64 Bytes.
/// </summary>
public static class TelegramCorrectionCallback
{
    private const string Prefix = "c";

    private static readonly (TelegramCorrectionAction Action, string Code)[] Codes =
    [
        (TelegramCorrectionAction.AskApprove, "ask-approve"),
        (TelegramCorrectionAction.AskReject, "ask-reject"),
        (TelegramCorrectionAction.Approve, "approve"),
        (TelegramCorrectionAction.Reject, "reject"),
        (TelegramCorrectionAction.Back, "back"),
    ];

    public static string Format(TelegramCorrectionAction action, string id)
        => $"{Prefix}:{Codes.First(entry => entry.Action == action).Code}:{id}";

    public static bool TryParse(string? data, out TelegramCorrectionAction action, out string id)
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
