using System.Globalization;
using Stempeluhr.Api.Models;

namespace Stempeluhr.Api.Services;

/// <summary>Abbildung der Abwesenheitsarten auf Kimai und deutsche Bezeichnungen.</summary>
public static class AbsenceKinds
{
    public static readonly IReadOnlyList<AbsenceKind> All = [AbsenceKind.Vacation, AbsenceKind.Sick, AbsenceKind.FlexDay];

    public static string Label(AbsenceKind kind) => kind switch
    {
        AbsenceKind.Vacation => "Urlaub",
        AbsenceKind.Sick => "Krank",
        _ => "Gleittag",
    };

    public static int? ActivityId(AbsenceKind kind, RuntimeSettings settings) => kind switch
    {
        AbsenceKind.Vacation => settings.VacationActivityId,
        AbsenceKind.Sick => settings.SickActivityId,
        _ => settings.FlexDayActivityId,
    };

    /// <summary>Die Art eines Kimai-Eintrags auf dem Abwesenheitsprojekt, sonst null.</summary>
    public static AbsenceKind? KindOf(KimaiTimesheetEntryDto entry, RuntimeSettings settings)
        => settings.AbsenceProjectId is { } project && entry.ProjectId == project
            ? All.Cast<AbsenceKind?>().FirstOrDefault(kind => ActivityId(kind!.Value, settings) == entry.ActivityId)
            : null;

    public static string Description(AbsenceKind kind) => $"{Label(kind)} (Stempeluhr)";

    /// <summary>Ein Gleittag deckt kein Soll: Dauer 0. Null, wenn die Stunden fehlen.</summary>
    public static TimeSpan? Duration(AbsenceKind kind, EmployeeSettings employee)
        => kind == AbsenceKind.FlexDay
            ? TimeSpan.Zero
            : employee.AbsenceDayHours is { } hours ? TimeSpan.FromHours((double)hours) : null;

    public static string Format(DateOnly day) => day.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);

    public static string FormatDays(decimal days) => days.ToString("0.##", CultureInfo.GetCultureInfo("de-DE"));
}

/// <summary>Lage eines Abwesenheitstags in Kimai, rein und ohne I/O.</summary>
public static class AbsencePlan
{
    public sealed record Placement(DateOnly Day, DateTimeOffset Begin, DateTimeOffset End);

    /// <summary>
    /// Beginn ist <paramref name="start"/> am Tag (Kimai-Zeitzone). Überlappt
    /// ein bestehender Eintrag, etwa das Ende einer Nachtschicht vom Vortag,
    /// beginnt die Abwesenheit direkt nach dessen Ende (auf die volle Minute
    /// aufgerundet). Passt sie dann nicht mehr in den Kalendertag, gibt es
    /// statt einer Lage eine Meldung. Die Dauer ist absolut: am Tag der
    /// Zeitumstellung endet ein 8-Stunden-Eintrag nach 8 echten Stunden.
    /// </summary>
    public static (Placement? Placement, string? Error) Place(
        DateOnly day,
        TimeOnly start,
        TimeSpan duration,
        TimeZoneInfo timeZone,
        IEnumerable<KimaiTimesheetEntryDto> entries)
    {
        var dayEnd = StartOfDay(day.AddDays(1), timeZone);
        var begin = ToInstant(day.ToDateTime(start), timeZone);
        var others = entries.Where(entry => entry.Begin is not null).ToArray();

        while (others.FirstOrDefault(entry => Overlaps(entry, begin, begin + duration)) is { } blocking)
        {
            if (blocking.End is not { } blockingEnd)
            {
                return (null, $"Am {AbsenceKinds.Format(day)} läuft gerade ein Eintrag. Bitte nach dem Ausstempeln erneut versuchen.");
            }

            begin = CeilingToMinute(blockingEnd);
        }

        var end = begin + duration;
        if (begin >= dayEnd || end > dayEnd)
        {
            return (null, $"Am {AbsenceKinds.Format(day)} ist kein Platz mehr für den Eintrag: bestehende Einträge reichen bis "
                + $"{TimeZoneInfo.ConvertTime(begin, timeZone):HH:mm} Uhr.");
        }

        return (new Placement(day, begin, end), null);
    }

    /// <summary>Ein Eintrag dieses Tages auf genau dieser Abwesenheits-Aktivität (Idempotenz).</summary>
    public static KimaiTimesheetEntryDto? FindExisting(
        DateOnly day, int projectId, int activityId, IEnumerable<KimaiTimesheetEntryDto> entries, TimeZoneInfo timeZone)
        => entries.FirstOrDefault(entry => entry.Id > 0 && entry.ProjectId == projectId && entry.ActivityId == activityId
            && entry.Begin is { } begin && LocalDay(begin, timeZone) == day);

    public static DateOnly LocalDay(DateTimeOffset value, TimeZoneInfo timeZone)
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, timeZone).DateTime);

    public static DateTimeOffset StartOfDay(DateOnly day, TimeZoneInfo timeZone)
        => ToInstant(day.ToDateTime(TimeOnly.MinValue), timeZone);

    /// <summary>
    /// Lokale Zeit als Zeitpunkt. Fällt sie in die Lücke der Umstellung auf
    /// Sommerzeit, gilt die erste gültige Minute danach; gibt es sie im Herbst
    /// zweimal, die erste (Sommerzeit), wie bei den Korrekturanträgen.
    /// </summary>
    public static DateTimeOffset ToInstant(DateTime local, TimeZoneInfo timeZone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        while (timeZone.IsInvalidTime(local))
        {
            local = local.AddMinutes(1);
        }

        var offset = timeZone.IsAmbiguousTime(local)
            ? timeZone.GetAmbiguousTimeOffsets(local).Max()
            : timeZone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset);
    }

    // Wie Kimai: [Beginn, Ende) überschneiden sich. Ein Gleittag (Dauer 0)
    // liegt in einem Eintrag, wenn dieser vor ihm beginnt und nach ihm endet.
    // Ein laufender Eintrag reicht ohne Ende in die Zukunft.
    private static bool Overlaps(KimaiTimesheetEntryDto entry, DateTimeOffset begin, DateTimeOffset end)
    {
        var entryBegin = entry.Begin!.Value;
        var entryEnd = entry.End ?? DateTimeOffset.MaxValue;
        return entryEnd > begin && (end > begin ? entryBegin < end : entryBegin <= begin);
    }

    private static DateTimeOffset CeilingToMinute(DateTimeOffset value)
    {
        var remainder = value.Ticks % TimeSpan.TicksPerMinute;
        return remainder == 0 ? value : value.AddTicks(TimeSpan.TicksPerMinute - remainder);
    }
}
