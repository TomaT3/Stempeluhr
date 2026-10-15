using Stempeluhr.Api.Models;

namespace Stempeluhr.Api.Services;

/// <summary>
/// Meldet Abwesenheitsanträge nach außen (Telegram folgt in #137). Fehler
/// dürfen nie eine Entscheidung verhindern: der Service fängt jede Ausnahme.
/// </summary>
public interface IAbsenceNotifier
{
    /// <summary>Ein neuer Antrag eines Mitarbeiters wartet auf Entscheidung.</summary>
    Task OnSubmitted(AbsenceRequest request, CancellationToken cancellationToken = default);

    /// <summary>Der Antrag wurde entschieden, zurückgezogen, ist gescheitert oder storniert.</summary>
    Task OnDecided(AbsenceRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Meldet nichts.</summary>
public sealed class NullAbsenceNotifier : IAbsenceNotifier
{
    public Task OnSubmitted(AbsenceRequest request, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task OnDecided(AbsenceRequest request, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
