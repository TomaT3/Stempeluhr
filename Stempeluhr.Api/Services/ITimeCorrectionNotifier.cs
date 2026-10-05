using Stempeluhr.Api.Models;

namespace Stempeluhr.Api.Services;

/// <summary>
/// Meldet Korrekturanträge nach außen (Telegram: <see cref="TelegramTimeCorrectionNotifier"/>).
/// Fehler dürfen nie eine Entscheidung verhindern: der Service fängt jede Ausnahme.
/// </summary>
public interface ITimeCorrectionNotifier
{
    /// <summary>Ein neuer Antrag wartet auf Entscheidung.</summary>
    Task OnSubmitted(TimeCorrectionRequest request, CancellationToken cancellationToken = default);

    /// <summary>Der Antrag wurde entschieden, zurückgezogen oder ist gescheitert.</summary>
    Task OnDecided(TimeCorrectionRequest request, CancellationToken cancellationToken = default);
}
