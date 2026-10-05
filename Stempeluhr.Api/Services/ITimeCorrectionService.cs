using Stempeluhr.Api.Models;

namespace Stempeluhr.Api.Services;

public interface ITimeCorrectionService
{
    // ---- Mitarbeiter (Kiosk, /clock): EmployeeId + PIN oder Karte ----

    /// <exception cref="PinLockedException">Der Mitarbeiter ist wegen falscher PINs gesperrt.</exception>
    Task<CorrectionResult<CorrectionTimesheetsDto>> GetSelectableShiftsAsync(
        CorrectionAuthRequest auth, CancellationToken cancellationToken = default);

    /// <exception cref="PinLockedException">Der Mitarbeiter ist wegen falscher PINs gesperrt.</exception>
    Task<CorrectionResult<TimeCorrectionDto>> SubmitAsync(
        SubmitCorrectionRequest request, CancellationToken cancellationToken = default);

    /// <summary>Die eigenen Anträge der letzten 31 Tage.</summary>
    /// <exception cref="PinLockedException">Der Mitarbeiter ist wegen falscher PINs gesperrt.</exception>
    Task<CorrectionResult<IReadOnlyList<TimeCorrectionDto>>> ListOwnAsync(
        CorrectionAuthRequest auth, CancellationToken cancellationToken = default);

    /// <summary>Nur eigene Anträge im Status Pending (sonst NotFound bzw. Conflict).</summary>
    /// <exception cref="PinLockedException">Der Mitarbeiter ist wegen falscher PINs gesperrt.</exception>
    Task<CorrectionResult<TimeCorrectionDto>> WithdrawAsync(
        CorrectionAuthRequest auth, string id, CancellationToken cancellationToken = default);

    // ---- Chef/Admin (Admin-Seite, später Telegram) ----

    /// <summary>Offene Anträge (Pending, Failed) oder alle gespeicherten, neueste zuerst.</summary>
    IReadOnlyList<TimeCorrectionDto> List(bool openOnly);

    /// <summary>
    /// Genehmigt und schreibt in Kimai. Kimai-Fehler ergeben Status Failed im
    /// Ergebnis, keine Ausnahme. Ein schon entschiedener Antrag wird
    /// unverändert zurückgegeben und bucht nichts.
    /// </summary>
    Task<CorrectionResult<TimeCorrectionDto>> ApproveAsync(
        string id, string decidedBy, CancellationToken cancellationToken = default);

    Task<CorrectionResult<TimeCorrectionDto>> RejectAsync(
        string id, string? note, string decidedBy, CancellationToken cancellationToken = default);

    /// <summary>Nur Failed: setzt beim ersten offenen Schritt fort.</summary>
    Task<CorrectionResult<TimeCorrectionDto>> RetryAsync(
        string id, string decidedBy, CancellationToken cancellationToken = default);

    /// <summary>Nur Failed: der Admin hat von Hand in Kimai nachgetragen.</summary>
    Task<CorrectionResult<TimeCorrectionDto>> ResolveManuallyAsync(
        string id, string decidedBy, CancellationToken cancellationToken = default);
}
