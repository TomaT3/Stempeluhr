using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Stempeluhr.Api.Models;

namespace Stempeluhr.Api.Services;

/// <summary>
/// Korrekturanträge: Mitarbeiter reichen sie ein (nur online, ohne
/// Offline-Queue), Chef oder Admin entscheidet, erst dann wird mit dem
/// Mitarbeiter-Token in Kimai geschrieben. Singleton: die Sperren pro Antrag
/// müssen alle Anfragen (Admin-Seite, Telegram) umfassen.
/// </summary>
public sealed class TimeCorrectionService(
    IRuntimeSettingsStore settingsStore,
    IEmployeeService employees,
    IKimaiClient kimai,
    TimeCorrectionStore store,
    ITimeCorrectionNotifier notifier,
    PinAttemptGuard? pinAttempts = null,
    ILogger<TimeCorrectionService>? logger = null,
    TimeProvider? clock = null) : ITimeCorrectionService
{
    private const int MaxSourceLength = 64;
    private const int MaxNoteLength = 300;
    private const int MaxErrorLength = 500;
    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm";

    /// <summary>
    /// Wie weit vor einem Zeitraum Timesheets für die Überlappungsprüfung
    /// gelesen werden: ein Eintrag, der vorher begann und in den Zeitraum
    /// hineinläuft, muss dabei sein.
    /// </summary>
    private static readonly TimeSpan OverlapLookback = TimeSpan.FromHours(48);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    // Prüfung "ein offener Antrag pro Timesheet" und Anlegen bilden eine
    // Einheit - sonst legen zwei gleichzeitige Anträge beide an.
    private readonly SemaphoreSlim _submitGate = new(1, 1);

    // ------------------------------------------------------------ Mitarbeiter

    public async Task<CorrectionResult<CorrectionTimesheetsDto>> GetSelectableShiftsAsync(
        CorrectionAuthRequest auth, CancellationToken cancellationToken = default)
    {
        var context = Authenticate(auth);
        if (context is null)
        {
            return Unauthorized<CorrectionTimesheetsDto>();
        }

        var (settings, employee) = context.Value;
        try
        {
            var now = _clock.GetUtcNow();
            var timeZone = await ResolveTimeZoneAsync(settings, employee, cancellationToken);
            var entries = await kimai.GetTimesheetsAsync(
                settings, employee,
                LocalNaive(now - TimeCorrectionValidator.MaxAge, timeZone), LocalNaive(now, timeZone),
                cancellationToken);

            var withRequest = store.List()
                .Where(request => request.IsOpen && request.EmployeeId == employee.Id && request.TimesheetId is not null)
                .Select(request => request.TimesheetId!.Value)
                .ToHashSet();
            var shifts = ShiftGrouper
                .Group(entries.Where(entry => entry.Id > 0 && entry.Begin is not null), entry => entry.Begin!.Value, entry => entry.End, now)
                .Select(shift => new CorrectionShiftDto(
                    Local(shift[0].Begin!.Value, timeZone),
                    shift.Any(entry => entry.End is null) ? null : Local(shift.Max(entry => entry.End!.Value), timeZone),
                    shift.Select(entry => ToEntryDto(entry, settings, employee, timeZone, withRequest)).ToArray()))
                .Reverse()
                .ToArray();
            return CorrectionResult<CorrectionTimesheetsDto>.Ok(new CorrectionTimesheetsDto(timeZone.Id, shifts));
        }
        catch (Exception ex) when (IsKimaiFailure(ex, cancellationToken))
        {
            logger?.LogWarning(ex, "Correction timesheets of {Employee} could not be read", employee.Id);
            return KimaiUnavailable<CorrectionTimesheetsDto>(ex);
        }
    }

    public async Task<CorrectionResult<TimeCorrectionDto>> SubmitAsync(
        SubmitCorrectionRequest request, CancellationToken cancellationToken = default)
    {
        var context = Authenticate(request.Auth);
        if (context is null)
        {
            return Unauthorized<TimeCorrectionDto>();
        }

        if (!TryParseKind(request.Kind, out var kind))
        {
            return Invalid<TimeCorrectionDto>("Unbekannte Art der Korrektur.");
        }

        var (settings, employee) = context.Value;
        TimeCorrectionRequest created;
        await _submitGate.WaitAsync(cancellationToken);
        try
        {
            var result = await BuildAndStoreAsync(request, kind, settings, employee, cancellationToken);
            if (result.Value is not { } stored)
            {
                return CorrectionResult<TimeCorrectionDto>.Fail(result.Outcome, result.Message!);
            }
            created = stored;
        }
        finally
        {
            _submitGate.Release();
        }

        await NotifyAsync(() => notifier.OnSubmitted(created, CancellationToken.None), "submitted", created.Id);
        return CorrectionResult<TimeCorrectionDto>.Ok(ToDto(created, settings, forEmployee: true));
    }

    public Task<CorrectionResult<IReadOnlyList<TimeCorrectionDto>>> ListOwnAsync(
        CorrectionAuthRequest auth, CancellationToken cancellationToken = default)
    {
        var context = Authenticate(auth);
        if (context is null)
        {
            return Task.FromResult(Unauthorized<IReadOnlyList<TimeCorrectionDto>>());
        }

        var (settings, employee) = context.Value;
        var since = _clock.GetUtcNow() - TimeCorrectionValidator.MaxAge;
        IReadOnlyList<TimeCorrectionDto> own = store.List()
            .Where(request => request.EmployeeId == employee.Id && request.CreatedAt >= since)
            .Select(request => ToDto(request, settings, forEmployee: true))
            .ToArray();
        return Task.FromResult(CorrectionResult<IReadOnlyList<TimeCorrectionDto>>.Ok(own));
    }

    public async Task<CorrectionResult<TimeCorrectionDto>> WithdrawAsync(
        CorrectionAuthRequest auth, string id, CancellationToken cancellationToken = default)
    {
        var context = Authenticate(auth);
        if (context is null)
        {
            return Unauthorized<TimeCorrectionDto>();
        }

        var (settings, employee) = context.Value;
        TimeCorrectionRequest withdrawn;
        using (await LockAsync(id, cancellationToken))
        {
            // Ein fremder Antrag ist für diesen Mitarbeiter nicht vorhanden.
            if (store.Find(id) is not { } current || current.EmployeeId != employee.Id)
            {
                return NotFound<TimeCorrectionDto>();
            }

            if (current.Status != TimeCorrectionStatus.Pending)
            {
                return CorrectionResult<TimeCorrectionDto>.Fail(
                    CorrectionOutcome.Conflict, "Der Antrag ist schon entschieden und kann nicht mehr zurückgezogen werden.");
            }

            withdrawn = current with
            {
                Status = TimeCorrectionStatus.Withdrawn,
                DecidedAt = _clock.GetUtcNow(),
                DecidedBy = employee.DisplayName,
            };
            store.Update(withdrawn);
        }

        await NotifyAsync(() => notifier.OnDecided(withdrawn, CancellationToken.None), "withdrawn", withdrawn.Id);
        return CorrectionResult<TimeCorrectionDto>.Ok(ToDto(withdrawn, settings, forEmployee: true));
    }

    // ------------------------------------------------------------ Chef/Admin

    public IReadOnlyList<TimeCorrectionDto> List(bool openOnly)
    {
        var settings = settingsStore.Load();
        return store.List()
            .Where(request => !openOnly || request.IsOpen)
            .Select(request => ToDto(request, settings, forEmployee: false))
            .ToArray();
    }

    public async Task<CorrectionResult<TimeCorrectionDto>> ApproveAsync(
        string id, string decidedBy, CancellationToken cancellationToken = default)
    {
        TimeCorrectionRequest result;
        using (await LockAsync(id, cancellationToken))
        {
            if (store.Find(id) is not { } current)
            {
                return NotFound<TimeCorrectionDto>();
            }

            // Doppelklick, Admin und Telegram zugleich: wer zu spät kommt,
            // sieht den Stand und löst keine zweite Buchung aus.
            if (current.Status != TimeCorrectionStatus.Pending)
            {
                return Current(current);
            }

            current = current with { DecidedAt = _clock.GetUtcNow(), DecidedBy = decidedBy, Error = null };
            store.Update(current);
            result = await ApplyAsync(current);
        }

        await NotifyAsync(() => notifier.OnDecided(result, CancellationToken.None), "decided", result.Id);
        return Current(result);
    }

    public async Task<CorrectionResult<TimeCorrectionDto>> RejectAsync(
        string id, string? note, string decidedBy, CancellationToken cancellationToken = default)
    {
        TimeCorrectionRequest rejected;
        using (await LockAsync(id, cancellationToken))
        {
            if (store.Find(id) is not { } current)
            {
                return NotFound<TimeCorrectionDto>();
            }

            if (current.Status != TimeCorrectionStatus.Pending)
            {
                return Current(current);
            }

            rejected = current with
            {
                Status = TimeCorrectionStatus.Rejected,
                DecidedAt = _clock.GetUtcNow(),
                DecidedBy = decidedBy,
                DecisionNote = Truncate(note?.Trim(), MaxNoteLength) is { Length: > 0 } trimmed ? trimmed : null,
            };
            store.Update(rejected);
        }

        await NotifyAsync(() => notifier.OnDecided(rejected, CancellationToken.None), "rejected", rejected.Id);
        return Current(rejected);
    }

    public async Task<CorrectionResult<TimeCorrectionDto>> RetryAsync(
        string id, string decidedBy, CancellationToken cancellationToken = default)
    {
        TimeCorrectionRequest result;
        using (await LockAsync(id, cancellationToken))
        {
            if (store.Find(id) is not { } current)
            {
                return NotFound<TimeCorrectionDto>();
            }

            if (current.Status != TimeCorrectionStatus.Failed)
            {
                return CorrectionResult<TimeCorrectionDto>.Fail(
                    CorrectionOutcome.Conflict, "Nur fehlgeschlagene Anträge können erneut versucht werden.");
            }

            // Pending, bis ApplyAsync ausgegangen ist: bricht der Prozess
            // dazwischen ab, setzt die nächste Genehmigung beim offenen Schritt fort.
            current = current with { Status = TimeCorrectionStatus.Pending, DecidedBy = decidedBy, Error = null };
            store.Update(current);
            result = await ApplyAsync(current);
        }

        await NotifyAsync(() => notifier.OnDecided(result, CancellationToken.None), "retried", result.Id);
        return Current(result);
    }

    public async Task<CorrectionResult<TimeCorrectionDto>> ResolveManuallyAsync(
        string id, string decidedBy, CancellationToken cancellationToken = default)
    {
        TimeCorrectionRequest resolved;
        using (await LockAsync(id, cancellationToken))
        {
            if (store.Find(id) is not { } current)
            {
                return NotFound<TimeCorrectionDto>();
            }

            if (current.Status != TimeCorrectionStatus.Failed)
            {
                return CorrectionResult<TimeCorrectionDto>.Fail(
                    CorrectionOutcome.Conflict, "Nur fehlgeschlagene Anträge können als erledigt markiert werden.");
            }

            resolved = current with
            {
                Status = TimeCorrectionStatus.ResolvedManually,
                DecidedAt = _clock.GetUtcNow(),
                DecidedBy = decidedBy,
            };
            store.Update(resolved);
        }

        await NotifyAsync(() => notifier.OnDecided(resolved, CancellationToken.None), "resolved", resolved.Id);
        return Current(resolved);
    }

    // ------------------------------------------------------------ Absenden

    private async Task<CorrectionResult<TimeCorrectionRequest>> BuildAndStoreAsync(
        SubmitCorrectionRequest input,
        TimeCorrectionKind kind,
        RuntimeSettings settings,
        EmployeeSettings employee,
        CancellationToken cancellationToken)
    {
        try
        {
            var now = _clock.GetUtcNow();
            var timeZone = await ResolveTimeZoneAsync(settings, employee, cancellationToken);

            var comment = input.Comment?.Trim();
            if (string.IsNullOrEmpty(comment))
            {
                comment = null;
            }

            var request = new TimeCorrectionRequest
            {
                Id = Guid.NewGuid().ToString("N"),
                EmployeeId = employee.Id,
                EmployeeName = employee.DisplayName,
                Kind = kind,
                Source = string.IsNullOrWhiteSpace(input.Source) ? "clock" : Truncate(input.Source.Trim(), MaxSourceLength)!,
                CreatedAt = now,
                Comment = comment,
                TimeZoneId = timeZone.Id,
            };

            if (TryParseTimes(input, timeZone) is not { } times)
            {
                return CorrectionResult<TimeCorrectionRequest>.Fail(CorrectionOutcome.Invalid, "Ungültige Zeitangabe.");
            }

            if (times.Error is not null)
            {
                return CorrectionResult<TimeCorrectionRequest>.Fail(CorrectionOutcome.Invalid, times.Error);
            }

            request = kind switch
            {
                TimeCorrectionKind.AddPause => request with { PauseBegin = times.PauseBegin, PauseEnd = times.PauseEnd },
                TimeCorrectionKind.SetEnd => request with { End = times.End },
                TimeCorrectionKind.AddShift => request with
                {
                    Begin = times.Begin, End = times.End, PauseBegin = times.PauseBegin, PauseEnd = times.PauseEnd,
                    TaskId = string.IsNullOrWhiteSpace(input.TaskId) ? null : input.TaskId.Trim(),
                },
                _ => request with { Begin = times.Begin, End = times.End },
            };

            if (kind != TimeCorrectionKind.AddShift)
            {
                if (input.TimesheetId is not { } timesheetId)
                {
                    return CorrectionResult<TimeCorrectionRequest>.Fail(CorrectionOutcome.Invalid, "Der Eintrag fehlt.");
                }

                var read = await ReadOwnTimesheetAsync(settings, employee, timesheetId, cancellationToken);
                if (read.Sheet is not { } sheet)
                {
                    return CorrectionResult<TimeCorrectionRequest>.Fail(CorrectionOutcome.Invalid, read.Error!);
                }

                request = request with { TimesheetId = sheet.Id, Original = ToOriginal(sheet) };

                // Der Kiosk zeigt Zeiten auf die Minute, Einträge aus Stempeln
                // und Offline-Nachtrag haben Sekunden: eine Zeit in der Minute
                // des bisherigen Werts meint genau diesen Wert.
                request = kind switch
                {
                    // Gleich gebliebene Zeit: keine Änderung, die Sekunden bleiben.
                    TimeCorrectionKind.ChangeTimes => request with
                    {
                        Begin = SameMinute(request.Begin, sheet.Begin) ? null : request.Begin,
                        End = SameMinute(request.End, sheet.End) ? null : request.End,
                    },
                    // Pause bis zum angezeigten Ende: keine Rest-Arbeit von ein paar Sekunden.
                    TimeCorrectionKind.AddPause when SameMinute(request.PauseEnd, sheet.End) => request with { PauseEnd = sheet.End },
                    // Ende in derselben Minute: kein Kürzen um Sekunden, der Validator lehnt es ab.
                    TimeCorrectionKind.SetEnd when SameMinute(request.End, sheet.End) => request with { End = sheet.End },
                    _ => request,
                };
            }
            else if (WorkTargetResolver.Resolve(settings, employee, request.TaskId) is null)
            {
                return CorrectionResult<TimeCorrectionRequest>.Fail(
                    CorrectionOutcome.Invalid, "Die Tätigkeit ist unbekannt oder nicht vollständig eingerichtet.");
            }

            if (request.PauseBegin is not null && WorkTargetResolver.ResolvePause(settings, employee) is null)
            {
                return CorrectionResult<TimeCorrectionRequest>.Fail(
                    CorrectionOutcome.Invalid, "Die Pausen-Aktivität ist nicht eingerichtet.");
            }

            var surrounding = await ReadSurroundingTimesheetsAsync(settings, employee, request, timeZone, cancellationToken);
            var open = store.List().Where(other => other.IsOpen).ToArray();
            if (TimeCorrectionValidator.Validate(request, surrounding, open, now, timeZone, settings, employee) is { } error)
            {
                return CorrectionResult<TimeCorrectionRequest>.Fail(CorrectionOutcome.Invalid, error);
            }

            store.Add(request);
            return CorrectionResult<TimeCorrectionRequest>.Ok(request);
        }
        catch (Exception ex) when (IsKimaiFailure(ex, cancellationToken))
        {
            logger?.LogWarning(ex, "Correction request of {Employee} could not be checked against Kimai", employee.Id);
            return KimaiUnavailable<TimeCorrectionRequest>(ex);
        }
    }

    /// <summary>
    /// Liest ein Timesheet und lässt nur eigene zu: Der Einzelabruf
    /// beschränkt der Token nicht auf den Besitzer (view_other_timesheet),
    /// also zählt die Kimai-User-ID. Fehlt sie irgendwo, wird abgelehnt statt
    /// geraten.
    /// </summary>
    private async Task<(KimaiTimesheetDetailDto? Sheet, string? Error)> ReadOwnTimesheetAsync(
        RuntimeSettings settings, EmployeeSettings employee, int timesheetId, CancellationToken cancellationToken)
    {
        if (await kimai.GetCurrentUserIdAsync(settings, employee, cancellationToken) is not { } ownUserId)
        {
            return (null, "Der Kimai-Benutzer konnte nicht ermittelt werden.");
        }

        var sheet = await kimai.GetTimesheetAsync(settings, employee, timesheetId, cancellationToken);
        return sheet is { UserId: { } userId } && userId == ownUserId
            ? (sheet, null)
            : (null, "Eintrag nicht gefunden.");
    }

    private async Task<IReadOnlyCollection<KimaiTimesheetEntryDto>> ReadSurroundingTimesheetsAsync(
        RuntimeSettings settings,
        EmployeeSettings employee,
        TimeCorrectionRequest request,
        TimeZoneInfo timeZone,
        CancellationToken cancellationToken)
    {
        var times = new[] { request.Begin, request.End, request.PauseBegin, request.PauseEnd, request.Original?.Begin, request.Original?.End }
            .Where(time => time is not null).Select(time => time!.Value).ToArray();
        if (times.Length == 0)
        {
            return [];
        }

        return await kimai.GetTimesheetsAsync(
            settings, employee,
            LocalNaive(times.Min() - OverlapLookback, timeZone), LocalNaive(times.Max(), timeZone),
            cancellationToken);
    }

    // ------------------------------------------------------------ Anwenden

    /// <summary>
    /// Schreibt einen genehmigten Antrag nach Kimai: Eintrag neu lesen und mit
    /// dem Snapshot vergleichen, Regeln erneut prüfen, dann die Schritte
    /// nacheinander ausführen und den Fortschritt je Schritt speichern.
    /// Läuft unter der Sperre des Antrags, bewusst ohne das Abbruch-Token der
    /// Anfrage: eine begonnene Buchung soll nicht an einem geschlossenen
    /// Browser-Tab hängen bleiben. Gibt den gespeicherten Antrag zurück,
    /// Applied oder Failed.
    /// </summary>
    private async Task<TimeCorrectionRequest> ApplyAsync(TimeCorrectionRequest request)
    {
        var cancellationToken = CancellationToken.None;
        try
        {
            var settings = settingsStore.Load();
            var employee = settings.Employees.FirstOrDefault(candidate =>
                candidate.CanClock && string.Equals(candidate.Id, request.EmployeeId, StringComparison.OrdinalIgnoreCase));
            if (employee is null)
            {
                return Fail(request, "Der Mitarbeiter ist nicht mehr aktiv.");
            }

            var timeZone = await ResolveTimeZoneAsync(settings, employee, cancellationToken);
            var steps = TimeCorrectionPlan.Steps(request);

            KimaiTimesheetDetailDto? sheet = null;
            if (request.TimesheetId is { } timesheetId)
            {
                var read = await ReadOwnTimesheetAsync(settings, employee, timesheetId, cancellationToken);
                if (read.Sheet is not { } own)
                {
                    return Fail(request, read.Error == "Eintrag nicht gefunden." ? "Eintrag nicht gefunden oder nicht erlaubt." : read.Error!);
                }

                if (!MatchesSnapshot(request, own, steps))
                {
                    return Fail(request, "Eintrag wurde inzwischen geändert");
                }

                sheet = own;
            }

            var surrounding = await ReadSurroundingTimesheetsAsync(settings, employee, request, timeZone, cancellationToken);
            var open = store.List().Where(other => other.IsOpen).ToArray();
            if (TimeCorrectionValidator.Validate(
                    request, surrounding, open, _clock.GetUtcNow(), timeZone, settings, employee, applying: true) is { } error)
            {
                return Fail(request, error);
            }

            var applied = request.AppliedSteps.ToList();
            foreach (var step in steps)
            {
                if (applied.Contains(step.Name))
                {
                    continue;
                }

                if (step.Kind == PlannedStepKind.Patch)
                {
                    // Schon im Zielzustand (Schritt ausgeführt, Fortschritt aber nicht mehr gespeichert).
                    if (sheet is not null && !IsPatched(sheet, request.Original!, step))
                    {
                        await kimai.UpdateTimesheetTimesAsync(
                            settings, employee, sheet.Id, step.Begin, step.End, cancellationToken);
                    }
                }
                else if (TimeCorrectionTargets.Resolve(step, request, settings, employee) is not { } resolved)
                {
                    return Fail(request with { AppliedSteps = applied.ToArray() },
                        "Projekt, Aktivität oder Pausen-Aktivität sind nicht mehr eingerichtet.");
                }
                else if (!await ExistsAsync(settings, employee, resolved.Target, step, timeZone, cancellationToken))
                {
                    await kimai.CreateTimesheetAsync(
                        settings, employee, resolved.Target, step.Begin!.Value, step.End!.Value, resolved.Description, cancellationToken);
                }

                applied.Add(step.Name);
                request = request with { AppliedSteps = applied.ToArray() };
                store.Update(request);
            }

            request = request with { Status = TimeCorrectionStatus.Applied, Error = null };
            store.Update(request);
            return request;
        }
        catch (Exception ex) when (IsKimaiFailure(ex, cancellationToken))
        {
            logger?.LogWarning(ex, "Correction request {Id} of {Employee} failed in Kimai", request.Id, request.EmployeeId);
            // Der Fortschritt steht schon im Store; der Antrag aus dem Store
            // trägt die Schritte, die vor dem Fehler gelaufen sind.
            return Fail(store.Find(request.Id) ?? request, Describe(ex));
        }
    }

    private TimeCorrectionRequest Fail(TimeCorrectionRequest request, string message)
    {
        var failed = request with { Status = TimeCorrectionStatus.Failed, Error = Truncate(message, MaxErrorLength) };
        store.Update(failed);
        return failed;
    }

    /// <summary>
    /// Vergleicht das neu gelesene Timesheet mit dem Snapshot. Ist der
    /// Patch-Schritt als erledigt gespeichert, zählt nur noch der Zustand
    /// danach: ein Rücksprung zum Original ist eine zwischenzeitliche Änderung,
    /// und der übersprungene Patch würde die Arbeit doppelt buchen. Sonst ist
    /// neben dem Original auch der gepatchte Zustand zulässig (Patch gelaufen,
    /// Fortschritt nicht mehr gespeichert). Jede andere Abweichung ist ein Konflikt.
    /// </summary>
    private static bool MatchesSnapshot(
        TimeCorrectionRequest request, KimaiTimesheetDetailDto sheet, IReadOnlyList<PlannedStep> steps)
    {
        if (request.Original is not { } original
            || sheet.ActivityId != original.ActivityId
            || sheet.ProjectId != original.ProjectId
            || sheet.Billable != original.Billable
            || (sheet.Description ?? "") != (original.Description ?? ""))
        {
            return false;
        }

        var patch = steps.FirstOrDefault(step => step.Kind == PlannedStepKind.Patch);
        if (patch is not null && request.AppliedSteps.Contains(patch.Name))
        {
            return IsPatched(sheet, original, patch);
        }

        return (sheet.Begin == original.Begin && sheet.End == original.End)
            || (patch is not null && IsPatched(sheet, original, patch));
    }

    /// <summary>
    /// Erwarteter Zustand nach dem Patch-Schritt: der Snapshot, nur mit den
    /// gepatchten Feldern ersetzt. Alle anderen Zeitfelder bleiben am Snapshot
    /// gemessen - sonst ginge ein zwischenzeitlich geänderter Beginn durch.
    /// </summary>
    private static bool IsPatched(KimaiTimesheetDetailDto sheet, TimeCorrectionOriginal original, PlannedStep patch)
        => sheet.Begin == (patch.Begin ?? original.Begin) && sheet.End == (patch.End ?? original.End);

    /// <summary>
    /// Gibt es den Eintrag schon (Beginn, Ende, Aktivität)? Dann hat ein
    /// früherer Versuch ihn angelegt, und der Schritt wird übersprungen.
    /// </summary>
    private async Task<bool> ExistsAsync(
        RuntimeSettings settings,
        EmployeeSettings employee,
        KimaiTimesheetTarget target,
        PlannedStep step,
        TimeZoneInfo timeZone,
        CancellationToken cancellationToken)
    {
        var begin = step.Begin!.Value;
        var entries = await kimai.GetTimesheetsAsync(
            settings, employee,
            LocalNaive(begin - TimeSpan.FromMinutes(1), timeZone), LocalNaive(begin + TimeSpan.FromMinutes(1), timeZone),
            cancellationToken);
        return entries.Any(entry => TimeCorrectionTargets.IsCreatedBy(entry, step, target.ActivityId));
    }

    // ------------------------------------------------------------ Hilfen

    private (RuntimeSettings Settings, EmployeeSettings Employee)? Authenticate(CorrectionAuthRequest auth)
    {
        var settings = settingsStore.Load();
        var employee = ClockService.FindEmployeeForClockAction(
            employees, pinAttempts, settings, auth.EmployeeId, auth.Pin, auth.NfcCardId);
        return employee is null ? null : (settings, employee);
    }

    private async Task<TimeZoneInfo> ResolveTimeZoneAsync(
        RuntimeSettings settings, EmployeeSettings employee, CancellationToken cancellationToken)
        => ClockService.ResolveTimezone(await kimai.GetCurrentUserTimezoneAsync(settings, employee, cancellationToken));

    private async Task<IDisposable> LockAsync(string id, CancellationToken cancellationToken)
    {
        var gate = _locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        return new Releaser(gate);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }

    private async Task NotifyAsync(Func<Task> notify, string what, string id)
    {
        try
        {
            await notify();
        }
        catch (Exception ex)
        {
            // Eine Meldung nach außen darf nie eine Entscheidung zurückdrehen.
            logger?.LogWarning(ex, "Correction request {Id}: notification '{What}' failed", id, what);
        }
    }

    private static bool IsKimaiFailure(Exception exception, CancellationToken cancellationToken)
        => exception is KimaiApiException or HttpRequestException or JsonException or InvalidOperationException
            || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested);

    private static string Describe(Exception exception) => exception switch
    {
        KimaiApiException api => api.Message,
        HttpRequestException or TaskCanceledException => "Kimai ist nicht erreichbar.",
        _ => exception.Message,
    };

    private static CorrectionResult<T> KimaiUnavailable<T>(Exception exception) => exception is KimaiApiException { IsTransient: false }
        ? CorrectionResult<T>.Fail(CorrectionOutcome.Invalid, "Kimai hat die Anfrage abgelehnt.")
        : CorrectionResult<T>.Fail(CorrectionOutcome.Unavailable, "Kimai ist gerade nicht erreichbar. Bitte später erneut versuchen.");

    private static CorrectionResult<T> Unauthorized<T>() => CorrectionResult<T>.Fail(CorrectionOutcome.Unauthorized, "Nicht angemeldet.");

    private static CorrectionResult<T> NotFound<T>() => CorrectionResult<T>.Fail(CorrectionOutcome.NotFound, "Antrag nicht gefunden.");

    private static CorrectionResult<T> Invalid<T>(string message) => CorrectionResult<T>.Fail(CorrectionOutcome.Invalid, message);

    private CorrectionResult<TimeCorrectionDto> Current(TimeCorrectionRequest request)
        => CorrectionResult<TimeCorrectionDto>.Ok(ToDto(request, settingsStore.Load(), forEmployee: false));

    private static string? Truncate(string? value, int length) => value is { } v && v.Length > length ? v[..length] : value;

    private static bool TryParseKind(string? value, out TimeCorrectionKind kind)
    {
        // Nur die vier benannten Arten, keine Zahlen: Enum.TryParse nähme "7".
        var name = Enum.GetNames<TimeCorrectionKind>()
            .FirstOrDefault(candidate => string.Equals(candidate, value?.Trim(), StringComparison.OrdinalIgnoreCase));
        kind = name is null ? default : Enum.Parse<TimeCorrectionKind>(name);
        return name is not null;
    }

    private static bool SameMinute(DateTimeOffset? requested, DateTimeOffset? current)
        => requested is { } a && current is { } b && a.UtcTicks / TimeSpan.TicksPerMinute == b.UtcTicks / TimeSpan.TicksPerMinute;

    private static TimeCorrectionOriginal ToOriginal(KimaiTimesheetDetailDto sheet)
        => new(sheet.Begin, sheet.End, sheet.ActivityId, sheet.ProjectId, sheet.Description, sheet.Billable);

    // ------------------------------------------------------------ Zeiten

    private sealed record ParsedTimes(
        DateTimeOffset? Begin, DateTimeOffset? End, DateTimeOffset? PauseBegin, DateTimeOffset? PauseEnd, string? Error);

    /// <summary>Null, wenn eine Angabe kein gültiges <c>yyyy-MM-ddTHH:mm</c> ist.</summary>
    private static ParsedTimes? TryParseTimes(SubmitCorrectionRequest input, TimeZoneInfo timeZone)
    {
        DateTimeOffset? begin = null, end = null, pauseBegin = null, pauseEnd = null;
        string? error = null;
        foreach (var (text, assign) in new (string? Text, Action<DateTimeOffset> Assign)[]
        {
            (input.Begin, value => begin = value),
            (input.End, value => end = value),
            (input.PauseBegin, value => pauseBegin = value),
            (input.PauseEnd, value => pauseEnd = value),
        })
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (!DateTime.TryParseExact(text.Trim(), TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            {
                return null;
            }

            if (timeZone.IsInvalidTime(local))
            {
                error = "Diese Uhrzeit gibt es wegen der Zeitumstellung nicht.";
                continue;
            }

            // Zeitumstellung im Herbst: die Stunde gibt es zweimal - die erste (Sommerzeit) gilt.
            var offset = timeZone.IsAmbiguousTime(local)
                ? timeZone.GetAmbiguousTimeOffsets(local).Max()
                : timeZone.GetUtcOffset(local);
            assign(new DateTimeOffset(local, offset));
        }

        return new ParsedTimes(begin, end, pauseBegin, pauseEnd, error);
    }

    private static DateTime LocalNaive(DateTimeOffset value, TimeZoneInfo timeZone)
        => TimeZoneInfo.ConvertTime(value, timeZone).DateTime;

    private static string Local(DateTimeOffset value, TimeZoneInfo timeZone)
        => TimeZoneInfo.ConvertTime(value, timeZone).ToString(TimeFormat, CultureInfo.InvariantCulture);

    private static string? Local(DateTimeOffset? value, TimeZoneInfo timeZone) => value is { } v ? Local(v, timeZone) : null;

    // ------------------------------------------------------------ DTOs

    private static CorrectionEntryDto ToEntryDto(
        KimaiTimesheetEntryDto entry,
        RuntimeSettings settings,
        EmployeeSettings employee,
        TimeZoneInfo timeZone,
        HashSet<int> withOpenRequest)
    {
        var (kind, label) = EntryKindAndLabel(settings, employee, entry.ProjectId, entry.ActivityId);
        return new CorrectionEntryDto(
            entry.Id,
            Local(entry.Begin!.Value, timeZone),
            Local(entry.End, timeZone),
            kind,
            label,
            withOpenRequest.Contains(entry.Id));
    }

    /// <summary>"pause" bei der Pausen-Aktivität, sonst "work" mit der Bezeichnung der passenden Tätigkeit.</summary>
    private static (string Kind, string Label) EntryKindAndLabel(
        RuntimeSettings settings, EmployeeSettings? employee, int? projectId, int? activityId)
    {
        if (settings.PauseActivityId is not null && activityId == settings.PauseActivityId)
        {
            return ("pause", "Pause");
        }

        var task = employee is not null ? WorkTargetResolver.MatchTask(employee, projectId, activityId) : null;
        return ("work", task?.Label ?? "Arbeit");
    }

    private static CorrectionOriginalDto ToOriginalDto(
        TimeCorrectionOriginal original, RuntimeSettings settings, EmployeeSettings? employee, TimeZoneInfo timeZone)
    {
        var (kind, label) = EntryKindAndLabel(settings, employee, original.ProjectId, original.ActivityId);
        return new CorrectionOriginalDto(
            Local(original.Begin, timeZone), Local(original.End, timeZone), original.Description, kind, label);
    }

    /// <summary>
    /// <paramref name="forEmployee"/>: Der Mitarbeiter sieht bei einem
    /// gescheiterten Antrag keine Kimai-Fehlertexte, nur den Hinweis.
    /// </summary>
    private static TimeCorrectionDto ToDto(TimeCorrectionRequest request, RuntimeSettings settings, bool forEmployee)
    {
        var timeZone = ClockService.ResolveTimezone(request.TimeZoneId);
        var employee = settings.Employees.FirstOrDefault(candidate => candidate.Id == request.EmployeeId);
        var taskLabel = request.TaskId is { } taskId && employee is not null
            ? WorkTargetResolver.ResolveTask(employee, taskId)?.Label
            : null;
        return new TimeCorrectionDto(
            request.Id,
            request.EmployeeId,
            request.EmployeeName,
            request.Kind,
            request.Status,
            request.Source,
            request.CreatedAt,
            request.Comment,
            timeZone.Id,
            request.TimesheetId,
            Local(request.Begin, timeZone),
            Local(request.End, timeZone),
            Local(request.PauseBegin, timeZone),
            Local(request.PauseEnd, timeZone),
            request.TaskId,
            taskLabel,
            request.Original is { } original
                ? ToOriginalDto(original, settings, employee, timeZone)
                : null,
            request.DecidedAt,
            request.DecidedBy,
            request.DecisionNote,
            forEmployee && request.Error is not null
                ? "Der Antrag konnte nicht in Kimai gebucht werden. Der Chef kümmert sich darum."
                : request.Error,
            request.AppliedSteps);
    }
}
