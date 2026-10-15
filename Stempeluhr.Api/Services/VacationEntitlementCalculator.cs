using Stempeluhr.Api.Models;

namespace Stempeluhr.Api.Services;

/// <summary>
/// Urlaubskonto eines Kalenderjahres, rein und ohne I/O (wie
/// <see cref="HoursOverviewCalculator"/>).
///
/// Anspruch = Jahresurlaub × volle Beschäftigungsmonate / 12. Ein Monat ist
/// voll, wenn der Mitarbeiter am 1. und am letzten Tag beschäftigt ist; ohne
/// Ein- bzw. Austrittsdatum ist die Seite offen. Ein Bruchteil ab 0,5 Tagen
/// wird auf einen ganzen Tag aufgerundet (§ 5 Abs. 2 BUrlG), kleinere bleiben
/// als Dezimalwert stehen. Rechtlich hat, wer vor dem 1. Juli eintritt und das
/// Jahr über bleibt, nach der Wartezeit Anspruch auf den vollen gesetzlichen
/// Mindesturlaub (§§ 4, 5 BUrlG) - das gleicht der Admin über die Korrektur aus.
/// </summary>
public static class VacationEntitlementCalculator
{
    /// <param name="requests">Anträge des Mitarbeiters; gezählt werden nur Urlaubstage im <paramref name="year"/>.</param>
    /// <param name="asOf">
    /// Stichtag: genommen sind dann nur angewandte Tage bis einschließlich
    /// <paramref name="asOf"/>, spätere zählen wie Beantragtes als geplant.
    /// Ohne Stichtag zählt jeder angewandte Tag als genommen. Der Rest ist in
    /// beiden Fällen derselbe.
    /// </param>
    public static VacationBalanceDto Calculate(
        EmployeeSettings employee, int year, IEnumerable<AbsenceRequest> requests, DateOnly? asOf = null)
    {
        var account = employee.VacationYears.FirstOrDefault(candidate => candidate.Year == year);
        var carryOver = account?.CarryOverDays ?? 0m;
        var correction = account?.CorrectionDays ?? 0m;
        var entitlement = Entitlement(employee, year);

        decimal taken = 0m, planned = 0m;
        foreach (var request in requests.Where(request => request.Kind == AbsenceKind.Vacation
            && request.EmployeeId == employee.Id && request.BlocksDays))
        {
            foreach (var day in request.Days.Where(day => day.Year == year))
            {
                if (request.IsTaken && (asOf is null || day <= asOf))
                {
                    taken++;
                }
                else
                {
                    planned++;
                }
            }
        }

        return new VacationBalanceDto(
            year, entitlement, carryOver, correction, taken, planned,
            entitlement + carryOver + correction - taken - planned);
    }

    public static decimal Entitlement(EmployeeSettings employee, int year)
    {
        if (employee.VacationDaysPerYear is not { } perYear || perYear <= 0)
        {
            return 0m;
        }

        var raw = perYear * FullMonths(employee, year) / 12m;
        var whole = decimal.Floor(raw);
        return raw - whole >= 0.5m ? whole + 1 : decimal.Round(raw, 2);
    }

    /// <summary>Kalendermonate, in denen der Mitarbeiter am 1. und am letzten Tag beschäftigt ist.</summary>
    public static int FullMonths(EmployeeSettings employee, int year)
    {
        var months = 0;
        for (var month = 1; month <= 12; month++)
        {
            var first = new DateOnly(year, month, 1);
            var last = first.AddMonths(1).AddDays(-1);
            if ((employee.EntryDate is null || employee.EntryDate <= first)
                && (employee.ExitDate is null || employee.ExitDate >= last))
            {
                months++;
            }
        }
        return months;
    }
}
