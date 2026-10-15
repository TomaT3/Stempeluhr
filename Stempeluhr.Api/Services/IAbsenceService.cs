using Stempeluhr.Api.Models;

namespace Stempeluhr.Api.Services;

public interface IAbsenceService
{
    // ---- Mitarbeiter (Kiosk, /clock): EmployeeId + PIN oder Karte ----

    /// <summary>Urlaubskonten, Zeitfenster je Art und belegte Tage für den Kalender.</summary>
    /// <exception cref="PinLockedException">Der Mitarbeiter ist wegen falscher PINs gesperrt.</exception>
    Task<CorrectionResult<KioskAbsenceOverviewDto>> GetOverviewAsync(
        CorrectionAuthRequest auth, CancellationToken cancellationToken = default);

    /// <exception cref="PinLockedException">Der Mitarbeiter ist wegen falscher PINs gesperrt.</exception>
    Task<CorrectionResult<AbsenceDto>> SubmitAsync(
        SubmitAbsenceRequest request, CancellationToken cancellationToken = default);

    /// <summary>Offene Anträge und die der letzten 12 Monate, neueste zuerst.</summary>
    /// <exception cref="PinLockedException">Der Mitarbeiter ist wegen falscher PINs gesperrt.</exception>
    Task<CorrectionResult<IReadOnlyList<AbsenceDto>>> ListOwnAsync(
        CorrectionAuthRequest auth, CancellationToken cancellationToken = default);

    /// <summary>Nur eigene Anträge im Status Pending (sonst NotFound bzw. Conflict).</summary>
    /// <exception cref="PinLockedException">Der Mitarbeiter ist wegen falscher PINs gesperrt.</exception>
    Task<CorrectionResult<AbsenceDto>> WithdrawAsync(
        CorrectionAuthRequest auth, string id, CancellationToken cancellationToken = default);

    // ---- Chef/Admin (Admin-Seite, Telegram) ----

    /// <summary>Offene (Pending, Failed) oder alle Anträge, optional mit einem Tag im Jahr; neueste zuerst.</summary>
    IReadOnlyList<AbsenceDto> List(bool openOnly, int? year = null);

    /// <summary>Erfasst für einen Mitarbeiter und wendet sofort an; Urlaub über dem Rest ergibt nur eine Warnung.</summary>
    Task<CorrectionResult<AbsenceDto>> CreateForEmployeeAsync(
        AdminCreateAbsenceRequest request, string createdBy, CancellationToken cancellationToken = default);

    /// <summary>
    /// Genehmigt und schreibt in Kimai. Kimai-Fehler ergeben Status Failed,
    /// nur ein vor dem ersten Schreiben unerreichbares Kimai Unavailable. Ein
    /// schon entschiedener Antrag wird unverändert zurückgegeben.
    /// </summary>
    Task<CorrectionResult<AbsenceDto>> ApproveAsync(
        string id, string decidedBy, CancellationToken cancellationToken = default);

    Task<CorrectionResult<AbsenceDto>> RejectAsync(
        string id, string? note, string decidedBy, CancellationToken cancellationToken = default);

    /// <summary>Nur Failed: setzt beim ersten Tag ohne Kimai-Eintrag fort.</summary>
    Task<CorrectionResult<AbsenceDto>> RetryAsync(
        string id, string decidedBy, CancellationToken cancellationToken = default);

    /// <summary>Nur Failed: der Admin hat von Hand in Kimai nachgetragen.</summary>
    Task<CorrectionResult<AbsenceDto>> ResolveManuallyAsync(
        string id, string decidedBy, CancellationToken cancellationToken = default);

    /// <summary>
    /// Nur Applied: löscht die Kimai-Einträge (404 = schon gelöscht). Erst wenn
    /// alle weg sind, wird der Antrag Cancelled; sonst bleibt er Applied mit
    /// Fehler, und ein erneutes Storno setzt fort.
    /// </summary>
    Task<CorrectionResult<AbsenceDto>> CancelAsync(
        string id, string cancelledBy, CancellationToken cancellationToken = default);

    IReadOnlyList<AdminVacationBalanceDto> GetBalances(int year);

    /// <summary>Übertrag und Korrektur eines Jahres; alles 0 ohne Notiz entfernt den Eintrag.</summary>
    Task<CorrectionResult<AdminVacationBalanceDto>> UpdateVacationYearAsync(
        string employeeId, int year, UpdateVacationYearRequest request, CancellationToken cancellationToken = default);
}
