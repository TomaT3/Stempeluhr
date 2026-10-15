using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Stempeluhr.Api.Models;

namespace Stempeluhr.Api.Services;

/// <summary>
/// Abwesenheiten (Urlaub, Krank, Gleittag): Mitarbeiter beantragen nur online,
/// Chef oder Admin entscheidet, erst dann wird mit dem Mitarbeiter-Token pro
/// Tag ein Timesheet in Kimai angelegt. Abwesenheiten sind keine Stempel:
/// gestempelt wird weiter nur über <c>/api/kiosk/clock</c>. Singleton, wie
/// <see cref="TimeCorrectionService"/>: die Sperren pro Antrag müssen alle
/// Anfragen (Admin-Seite, Telegram) umfassen.
/// </summary>
public sealed class AbsenceService(
    IRuntimeSettingsStore settingsStore,
    IEmployeeService employees,
    IKimaiClient kimai,
    AbsenceStore store,
    IAbsenceNotifier notifier,
    PinAttemptGuard? pinAttempts = null,
    ILogger<AbsenceService>? logger = null,
    TimeProvider? clock = null) : IAbsenceService
{
    private const int MaxSourceLength = 64;
    private const int MaxNoteLength = 300;
    private const int MaxErrorLength = 500;
    private const decimal MaxAccountDays = 366m;
    private const string DayFormat = "yyyy-MM-dd";
    private static readonly TimeSpan OwnHistory = TimeSpan.FromDays(365);
    private static readonly JsonSerializerOptions SettingsJson = new(JsonSerializerDefaults.Web);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Task, byte> _backgroundNotifications = new();

    // Prüfung "höchstens ein Antrag pro Tag" und Anlegen bilden eine Einheit.
    private readonly SemaphoreSlim _submitGate = new(1, 1);

    // Laden, Ändern und Speichern der Urlaubskonten in settings.json.
    private readonly SemaphoreSlim _settingsGate = new(1, 1);

    // ------------------------------------------------------------ Mitarbeiter

    public async Task<CorrectionResult<KioskAbsenceOverviewDto>> GetOverviewAsync(
        CorrectionAuthRequest auth, CancellationToken cancellationToken = default)
    {
        if (Authenticate(auth) is not { } context)
        {
            return Unauthorized<KioskAbsenceOverviewDto>();
        }

        var (settings, employee) = context;

        // Nur für "heute": ohne Kimai bleibt der Kalender mit der Serverzeit nutzbar.
        TimeZoneInfo timeZone;
        try
        {
            timeZone = await ResolveTimeZoneAsync(settings, employee, cancellationToken);
        }
        catch (Exception ex) when (IsKimaiFailure(ex, cancellationToken))
        {
            timeZone = TimeZoneInfo.Local;
        }

        var today = Today(timeZone);
        var own = store.List().Where(request => request.EmployeeId == employee.Id).ToArray();
        var lastYear = AbsenceValidator.Window(AbsenceKind.Vacation, today, isAdmin: false).Latest.Year;
        var balances = Enumerable.Range(today.Year, lastYear - today.Year + 1)
            .Select(year => VacationEntitlementCalculator.Calculate(employee, year, own))
            .ToArray();
        var kinds = AbsenceKinds.All
            .Select(kind =>
            {
                var (earliest, latest) = AbsenceValidator.Window(kind, today, isAdmin: false);
                return new AbsenceKindDto(kind, Format(earliest), Format(latest), AbsenceValidator.MissingSettings(kind, settings, employee));
            })
            .ToArray();
        var days = own.Where(request => request.BlocksDays)
            .SelectMany(request => request.Days.Select(day => new AbsenceDayDto(Format(day), request.Kind, request.Status)))
            .OrderBy(day => day.Day, StringComparer.Ordinal)
            .ToArray();
        return CorrectionResult<KioskAbsenceOverviewDto>.Ok(new KioskAbsenceOverviewDto(Format(today), balances, kinds, days));
    }

    public async Task<CorrectionResult<AbsenceDto>> SubmitAsync(
        SubmitAbsenceRequest request, CancellationToken cancellationToken = default)
    {
        if (Authenticate(request.Auth) is not { } context)
        {
            return Unauthorized<AbsenceDto>();
        }

        var (settings, employee) = context;

        var source = string.IsNullOrWhiteSpace(request.Source) ? "clock" : Truncate(request.Source.Trim(), MaxSourceLength)!;
        if (source == AbsenceRequest.AdminSource)
        {
            source = "clock";
        }

        var created = await CreateAsync(
            settings, employee, request.Kind, request.Days, request.Comment, source, employee.DisplayName, isAdmin: false, cancellationToken);
        if (created.Value is not { } stored)
        {
            return CorrectionResult<AbsenceDto>.Fail(created.Outcome, created.Message!);
        }

        // Im Hintergrund wie bei den Korrekturen: der Kiosk bricht nach wenigen
        // Sekunden ab, ein langsamer Notifier darf keinen Doppelantrag auslösen.
        NotifyInBackground(() => notifier.OnSubmitted(stored.Request, CancellationToken.None), "submitted", stored.Request.Id);
        return CorrectionResult<AbsenceDto>.Ok(ToDto(stored.Request, forEmployee: true));
    }

    public Task<CorrectionResult<IReadOnlyList<AbsenceDto>>> ListOwnAsync(
        CorrectionAuthRequest auth, CancellationToken cancellationToken = default)
    {
        if (Authenticate(auth) is not { Employee: var employee })
        {
            return Task.FromResult(Unauthorized<IReadOnlyList<AbsenceDto>>());
        }

        var since = _clock.GetUtcNow() - OwnHistory;
        var sinceDay = DateOnly.FromDateTime(since.UtcDateTime);
        IReadOnlyList<AbsenceDto> own = store.List()
            .Where(request => request.EmployeeId == employee.Id
                && (request.IsOpen || request.CreatedAt >= since || request.Days.Any(day => day >= sinceDay)))
            .Select(request => ToDto(request, forEmployee: true))
            .ToArray();
        return Task.FromResult(CorrectionResult<IReadOnlyList<AbsenceDto>>.Ok(own));
    }

    public async Task<CorrectionResult<AbsenceDto>> WithdrawAsync(
        CorrectionAuthRequest auth, string id, CancellationToken cancellationToken = default)
    {
        if (Authenticate(auth) is not { Employee: var employee })
        {
            return Unauthorized<AbsenceDto>();
        }

        AbsenceRequest withdrawn;
        using (await LockAsync(id, cancellationToken))
        {
            // Ein fremder Antrag ist für diesen Mitarbeiter nicht vorhanden.
            if (store.Find(id) is not { } current || current.EmployeeId != employee.Id)
            {
                return NotFound<AbsenceDto>();
            }

            if (current.Status != AbsenceStatus.Pending)
            {
                return CorrectionResult<AbsenceDto>.Fail(
                    CorrectionOutcome.Conflict, "Der Antrag ist schon entschieden und kann nicht mehr zurückgezogen werden.");
            }

            withdrawn = current with
            {
                Status = AbsenceStatus.Withdrawn,
                DecidedAt = _clock.GetUtcNow(),
                DecidedBy = employee.DisplayName,
            };
            store.Update(withdrawn);
        }

        NotifyInBackground(() => notifier.OnDecided(withdrawn, CancellationToken.None), "withdrawn", withdrawn.Id);
        return CorrectionResult<AbsenceDto>.Ok(ToDto(withdrawn, forEmployee: true));
    }

    // ------------------------------------------------------------ Chef/Admin

    public IReadOnlyList<AbsenceDto> List(bool openOnly, int? year = null)
        => store.List()
            .Where(request => !openOnly || request.IsOpen)
            .Where(request => year is not { } y || request.Days.Any(day => day.Year == y))
            .Select(request => ToDto(request, forEmployee: false))
            .ToArray();

    public async Task<CorrectionResult<AbsenceDto>> CreateForEmployeeAsync(
        AdminCreateAbsenceRequest request, string createdBy, CancellationToken cancellationToken = default)
    {
        var settings = settingsStore.Load();
        var employee = settings.Employees.FirstOrDefault(candidate =>
            candidate.CanClock && string.Equals(candidate.Id, request.EmployeeId?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (employee is null)
        {
            return CorrectionResult<AbsenceDto>.Fail(
                CorrectionOutcome.NotFound, "Mitarbeiter nicht gefunden, nicht aktiv oder ohne Kimai-Token.");
        }

        var created = await CreateAsync(
            settings, employee, request.Kind, request.Days, request.Comment,
            AbsenceRequest.AdminSource, createdBy, isAdmin: true, cancellationToken);
        if (created.Value is not { } stored)
        {
            return CorrectionResult<AbsenceDto>.Fail(created.Outcome, created.Message!);
        }

        AbsenceRequest result;
        using (await LockAsync(stored.Request.Id, cancellationToken))
        {
            var applied = await ApplyAsync(stored.Request);
            // Kimai fiel nach der Prüfung aus: als Failed festhalten, damit der
            // Admin den Antrag sieht und erneut versuchen kann.
            result = applied.Unavailable ? Fail(stored.Request, "Kimai ist nicht erreichbar.") : applied.Request;
        }

        return CorrectionResult<AbsenceDto>.Ok(ToDto(result, forEmployee: false) with { Warning = stored.Warning });
    }

    public async Task<CorrectionResult<AbsenceDto>> ApproveAsync(
        string id, string decidedBy, CancellationToken cancellationToken = default)
    {
        AbsenceRequest result;
        using (await LockAsync(id, cancellationToken))
        {
            if (store.Find(id) is not { } current)
            {
                return NotFound<AbsenceDto>();
            }

            // Doppelklick, Admin und Telegram zugleich: wer zu spät kommt,
            // sieht den Stand und löst keine zweite Buchung aus.
            if (current.Status != AbsenceStatus.Pending)
            {
                return Current(current);
            }

            var applied = await ApplyAsync(current with { DecidedAt = _clock.GetUtcNow(), DecidedBy = decidedBy, Error = null });
            if (applied.Unavailable)
            {
                // Nichts geschrieben: der Antrag bleibt offen (keine Offline-Queue).
                store.Update(current);
                return KimaiUnavailable<AbsenceDto>();
            }
            result = applied.Request;
        }

        await NotifyAsync(() => notifier.OnDecided(result, CancellationToken.None), "decided", result.Id);
        return Current(result);
    }

    public async Task<CorrectionResult<AbsenceDto>> RejectAsync(
        string id, string? note, string decidedBy, CancellationToken cancellationToken = default)
    {
        AbsenceRequest rejected;
        using (await LockAsync(id, cancellationToken))
        {
            if (store.Find(id) is not { } current)
            {
                return NotFound<AbsenceDto>();
            }

            if (current.Status != AbsenceStatus.Pending)
            {
                return Current(current);
            }

            rejected = current with
            {
                Status = AbsenceStatus.Rejected,
                DecidedAt = _clock.GetUtcNow(),
                DecidedBy = decidedBy,
                DecisionNote = Truncate(note?.Trim(), MaxNoteLength) is { Length: > 0 } trimmed ? trimmed : null,
            };
            store.Update(rejected);
        }

        await NotifyAsync(() => notifier.OnDecided(rejected, CancellationToken.None), "rejected", rejected.Id);
        return Current(rejected);
    }

    public async Task<CorrectionResult<AbsenceDto>> RetryAsync(
        string id, string decidedBy, CancellationToken cancellationToken = default)
    {
        AbsenceRequest result;
        using (await LockAsync(id, cancellationToken))
        {
            if (store.Find(id) is not { } current)
            {
                return NotFound<AbsenceDto>();
            }

            if (current.Status != AbsenceStatus.Failed)
            {
                return CorrectionResult<AbsenceDto>.Fail(
                    CorrectionOutcome.Conflict, "Nur fehlgeschlagene Anträge können erneut versucht werden.");
            }

            // Pending, bis ApplyAsync ausgegangen ist: bricht der Prozess
            // dazwischen ab, setzt die nächste Genehmigung beim offenen Tag fort.
            var retrying = current with { Status = AbsenceStatus.Pending, DecidedBy = decidedBy, Error = null };
            store.Update(retrying);
            var applied = await ApplyAsync(retrying);
            if (applied.Unavailable)
            {
                store.Update(current);
                return KimaiUnavailable<AbsenceDto>();
            }
            result = applied.Request;
        }

        await NotifyAsync(() => notifier.OnDecided(result, CancellationToken.None), "retried", result.Id);
        return Current(result);
    }

    public async Task<CorrectionResult<AbsenceDto>> ResolveManuallyAsync(
        string id, string decidedBy, CancellationToken cancellationToken = default)
    {
        AbsenceRequest resolved;
        using (await LockAsync(id, cancellationToken))
        {
            if (store.Find(id) is not { } current)
            {
                return NotFound<AbsenceDto>();
            }

            if (current.Status != AbsenceStatus.Failed)
            {
                return CorrectionResult<AbsenceDto>.Fail(
                    CorrectionOutcome.Conflict, "Nur fehlgeschlagene Anträge können als erledigt markiert werden.");
            }

            resolved = current with
            {
                Status = AbsenceStatus.ResolvedManually,
                DecidedAt = _clock.GetUtcNow(),
                DecidedBy = decidedBy,
            };
            store.Update(resolved);
        }

        await NotifyAsync(() => notifier.OnDecided(resolved, CancellationToken.None), "resolved", resolved.Id);
        return Current(resolved);
    }

    public async Task<CorrectionResult<AbsenceDto>> CancelAsync(
        string id, string cancelledBy, CancellationToken cancellationToken = default)
    {
        AbsenceRequest request;
        using (await LockAsync(id, cancellationToken))
        {
            if (store.Find(id) is not { } current)
            {
                return NotFound<AbsenceDto>();
            }

            // Doppelklick: schon storniert ist kein Fehler.
            if (current.Status == AbsenceStatus.Cancelled)
            {
                return Current(current);
            }

            if (current.Status != AbsenceStatus.Applied)
            {
                return CorrectionResult<AbsenceDto>.Fail(
                    CorrectionOutcome.Conflict, "Nur eingetragene Abwesenheiten können storniert werden.");
            }

            var settings = settingsStore.Load();
            var employee = settings.Employees.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, current.EmployeeId, StringComparison.OrdinalIgnoreCase));
            if (employee is null || string.IsNullOrWhiteSpace(employee.ApiToken))
            {
                request = current with { Error = "Storno nicht möglich: Der Mitarbeiter oder sein Kimai-Token fehlt." };
                store.Update(request);
                return Current(request);
            }

            request = current;
            var remaining = new Dictionary<string, int>(current.AppliedDays);
            try
            {
                foreach (var (day, timesheetId) in current.AppliedDays.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    // Bewusst ohne das Abbruch-Token der Anfrage, wie beim Anwenden.
                    await kimai.DeleteTimesheetAsync(settings, employee, timesheetId, CancellationToken.None);
                    remaining.Remove(day);
                    request = request with { AppliedDays = new Dictionary<string, int>(remaining) };
                    store.Update(request);
                }

                request = request with
                {
                    Status = AbsenceStatus.Cancelled,
                    CancelledAt = _clock.GetUtcNow(),
                    CancelledBy = cancelledBy,
                    Error = null,
                };
            }
            catch (Exception ex) when (IsKimaiFailure(ex, CancellationToken.None))
            {
                logger?.LogWarning(ex, "Cancelling absence {Id} of {Employee} failed in Kimai", current.Id, current.EmployeeId);
                request = request with { Error = Truncate($"Storno unvollständig: {Describe(ex)}", MaxErrorLength) };
            }
            store.Update(request);
        }

        await NotifyAsync(() => notifier.OnDecided(request, CancellationToken.None), "cancelled", request.Id);
        return Current(request);
    }

    public IReadOnlyList<AdminVacationBalanceDto> GetBalances(int year)
    {
        var settings = settingsStore.Load();
        var requests = store.List();
        return settings.Employees.Select(employee => ToBalanceDto(employee, year, requests)).ToArray();
    }

    public async Task<CorrectionResult<AdminVacationBalanceDto>> UpdateVacationYearAsync(
        string employeeId, int year, UpdateVacationYearRequest request, CancellationToken cancellationToken = default)
    {
        if (year is < 2000 or > 2100)
        {
            return Invalid<AdminVacationBalanceDto>("Ungültiges Jahr.");
        }

        if (Math.Abs(request.CarryOverDays) > MaxAccountDays || Math.Abs(request.CorrectionDays) > MaxAccountDays)
        {
            return Invalid<AdminVacationBalanceDto>($"Übertrag und Korrektur liegen zwischen -{MaxAccountDays} und {MaxAccountDays} Tagen.");
        }

        var note = request.Note?.Trim();
        if (note?.Length > MaxNoteLength)
        {
            return Invalid<AdminVacationBalanceDto>($"Die Notiz darf höchstens {MaxNoteLength} Zeichen haben.");
        }

        await _settingsGate.WaitAsync(cancellationToken);
        try
        {
            var settings = settingsStore.Load();
            var index = settings.Employees.FindIndex(candidate =>
                string.Equals(candidate.Id, employeeId, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return CorrectionResult<AdminVacationBalanceDto>.Fail(CorrectionOutcome.NotFound, "Mitarbeiter nicht gefunden.");
            }

            var employee = settings.Employees[index];
            var years = employee.VacationYears.Where(candidate => candidate.Year != year).ToList();
            if (request.CarryOverDays != 0 || request.CorrectionDays != 0 || !string.IsNullOrEmpty(note))
            {
                years.Add(new EmployeeVacationYear(year, request.CarryOverDays, request.CorrectionDays, string.IsNullOrEmpty(note) ? null : note));
            }

            var updated = WithVacationYears(employee, [.. years.OrderBy(candidate => candidate.Year)]);
            settings.Employees[index] = updated;
            await settingsStore.SaveAsync(settings, cancellationToken);
            return CorrectionResult<AdminVacationBalanceDto>.Ok(ToBalanceDto(updated, year, store.List()));
        }
        finally
        {
            _settingsGate.Release();
        }
    }

    // ------------------------------------------------------------ Anlegen

    private sealed record Created(AbsenceRequest Request, string? Warning);

    /// <summary>Prüft gegen Kimai und legt unter dem globalen Gate an (Status Pending).</summary>
    private async Task<CorrectionResult<Created>> CreateAsync(
        RuntimeSettings settings,
        EmployeeSettings employee,
        string? kindText,
        IReadOnlyList<string>? dayTexts,
        string? commentText,
        string source,
        string createdBy,
        bool isAdmin,
        CancellationToken cancellationToken)
    {
        if (!TryParseKind(kindText, out var kind))
        {
            return Invalid<Created>("Unbekannte Art der Abwesenheit.");
        }

        if (!TryParseDays(dayTexts, out var days))
        {
            return Invalid<Created>("Ungültiges Datum.");
        }

        var comment = string.IsNullOrWhiteSpace(commentText) ? null : commentText.Trim();

        // Fehlende Einstellungen ohne Kimai-Anfrage melden.
        if (AbsenceValidator.MissingSettings(kind, settings, employee) is { } missing)
        {
            return Invalid<Created>(missing);
        }

        await _submitGate.WaitAsync(cancellationToken);
        try
        {
            var timeZone = await ResolveTimeZoneAsync(settings, employee, cancellationToken);
            var entries = await ReadEntriesAsync(settings, employee, days, timeZone, cancellationToken);
            var validation = AbsenceValidator.Validate(new AbsenceValidationInput(
                kind, days, isAdmin, Today(timeZone), employee, settings,
                store.List().Where(other => other.EmployeeId == employee.Id), entries, timeZone, comment));
            if (validation.Error is { } error)
            {
                return Invalid<Created>(error);
            }

            var now = _clock.GetUtcNow();
            var request = new AbsenceRequest
            {
                Id = Guid.NewGuid().ToString("N"),
                EmployeeId = employee.Id,
                EmployeeName = employee.DisplayName,
                Kind = kind,
                Days = days,
                Source = source,
                CreatedAt = now,
                CreatedBy = createdBy,
                Comment = comment,
                // Die Admin-Erfassung gilt sofort als genehmigt.
                DecidedAt = isAdmin ? now : null,
                DecidedBy = isAdmin ? createdBy : null,
            };
            store.Add(request);
            return CorrectionResult<Created>.Ok(new Created(request, validation.Warning));
        }
        catch (Exception ex) when (IsKimaiFailure(ex, cancellationToken))
        {
            logger?.LogWarning(ex, "Absence request of {Employee} could not be checked against Kimai", employee.Id);
            return KimaiUnavailable<Created>(ex);
        }
        finally
        {
            _submitGate.Release();
        }
    }

    // ------------------------------------------------------------ Anwenden

    private sealed record ApplyOutcome(AbsenceRequest Request, bool Unavailable = false);

    /// <summary>
    /// Schreibt einen genehmigten Antrag nach Kimai: Regeln mit frischen
    /// Timesheets erneut prüfen (mit den Rechten des Admins), dann Tag für Tag
    /// einen Eintrag anlegen - oder einen vorhandenen derselben Art übernehmen -
    /// und den Fortschritt nach jedem Tag speichern. Läuft unter der Sperre des
    /// Antrags und bewusst ohne das Abbruch-Token der Anfrage. Ist Kimai
    /// unerreichbar, bevor etwas geschrieben wurde, bleibt der Store unberührt
    /// und das Ergebnis meldet <see cref="ApplyOutcome.Unavailable"/>.
    /// </summary>
    private async Task<ApplyOutcome> ApplyAsync(AbsenceRequest request)
    {
        var cancellationToken = CancellationToken.None;
        var wrote = false;
        try
        {
            var settings = settingsStore.Load();
            var employee = settings.Employees.FirstOrDefault(candidate =>
                candidate.CanClock && string.Equals(candidate.Id, request.EmployeeId, StringComparison.OrdinalIgnoreCase));
            if (employee is null)
            {
                return new(Fail(request, "Der Mitarbeiter ist nicht mehr aktiv."));
            }

            var timeZone = await ResolveTimeZoneAsync(settings, employee, cancellationToken);
            var entries = await ReadEntriesAsync(settings, employee, request.Days, timeZone, cancellationToken);
            var validation = AbsenceValidator.Validate(new AbsenceValidationInput(
                request.Kind, request.Days, IsAdmin: true, Today(timeZone), employee, settings,
                store.List().Where(other => other.EmployeeId == employee.Id && other.Id != request.Id),
                entries, timeZone, Applying: true));
            if (validation.Error is { } error)
            {
                return new(Fail(request, error));
            }

            var projectId = settings.AbsenceProjectId!.Value;
            var activityId = AbsenceKinds.ActivityId(request.Kind, settings)!.Value;
            var target = new KimaiTimesheetTarget(projectId, activityId, AbsenceKinds.Description(request.Kind), false, null, null);
            var duration = AbsenceKinds.Duration(request.Kind, employee)!.Value;
            var start = settings.AbsenceDayStartTime!.Value;

            var applied = new Dictionary<string, int>(request.AppliedDays);
            foreach (var day in request.Days)
            {
                var key = Format(day);
                if (applied.ContainsKey(key))
                {
                    continue;
                }

                int timesheetId;
                if (AbsencePlan.FindExisting(day, projectId, activityId, entries, timeZone) is { } existing)
                {
                    // Angelegt, aber die Antwort ging verloren - oder der Chef hat ihn in Kimai eingetragen.
                    timesheetId = existing.Id;
                }
                else
                {
                    var (placement, placementError) = AbsencePlan.Place(day, start, duration, timeZone, entries);
                    if (placement is null)
                    {
                        return new(Fail(request, placementError!));
                    }

                    wrote = true;
                    timesheetId = await kimai.CreateTimesheetAsync(
                        settings, employee, target, placement.Begin, placement.End, target.Description, cancellationToken);
                }

                applied[key] = timesheetId;
                request = request with { AppliedDays = new Dictionary<string, int>(applied) };
                store.Update(request);
            }

            request = request with { Status = AbsenceStatus.Applied, Error = null };
            store.Update(request);
            return new(request);
        }
        catch (Exception ex) when (IsKimaiFailure(ex, cancellationToken))
        {
            if (!wrote && IsUnreachable(ex))
            {
                logger?.LogWarning(ex, "Absence {Id} of {Employee}: Kimai unreachable, nothing written", request.Id, request.EmployeeId);
                return new(request, Unavailable: true);
            }

            logger?.LogWarning(ex, "Absence {Id} of {Employee} failed in Kimai", request.Id, request.EmployeeId);
            // request trägt den gespeicherten Fortschritt bis zum Fehler.
            return new(Fail(request, Describe(ex)));
        }
    }

    private AbsenceRequest Fail(AbsenceRequest request, string message)
    {
        var failed = request with { Status = AbsenceStatus.Failed, Error = Truncate(message, MaxErrorLength) };
        store.Update(failed);
        return failed;
    }

    /// <summary>
    /// Timesheets rund um die Tage, ab 00:00 des Vortags (das Ende einer
    /// Nachtschicht). Nahe beieinander liegende Tage teilen sich eine Abfrage.
    /// </summary>
    private async Task<IReadOnlyCollection<KimaiTimesheetEntryDto>> ReadEntriesAsync(
        RuntimeSettings settings,
        EmployeeSettings employee,
        IReadOnlyList<DateOnly> days,
        TimeZoneInfo timeZone,
        CancellationToken cancellationToken)
    {
        var entries = new List<KimaiTimesheetEntryDto>();
        foreach (var (first, last) in Clusters(days))
        {
            entries.AddRange(await kimai.GetTimesheetsAsync(
                settings, employee,
                first.AddDays(-1).ToDateTime(TimeOnly.MinValue),
                last.ToDateTime(TimeOnly.MaxValue),
                cancellationToken));
        }
        return entries.DistinctBy(entry => entry.Id).ToArray();
    }

    private static IEnumerable<(DateOnly First, DateOnly Last)> Clusters(IReadOnlyList<DateOnly> days)
    {
        if (days.Count == 0)
        {
            yield break;
        }

        var (first, last) = (days[0], days[0]);
        foreach (var day in days.Skip(1))
        {
            if (day.DayNumber - last.DayNumber > 7)
            {
                yield return (first, last);
                first = day;
            }
            last = day;
        }
        yield return (first, last);
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

    private DateOnly Today(TimeZoneInfo timeZone) => AbsencePlan.LocalDay(_clock.GetUtcNow(), timeZone);

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

    private void NotifyInBackground(Func<Task> notify, string what, string id)
    {
        var task = Task.Run(() => NotifyAsync(notify, what, id));
        _backgroundNotifications[task] = 0;
        _ = task.ContinueWith(finished =>
        {
            _backgroundNotifications.TryRemove(finished, out _);
            if (finished.IsFaulted)
            {
                logger?.LogError(finished.Exception, "Absence {Id}: notification '{What}' crashed", id, what);
            }
        }, TaskScheduler.Default);
    }

    /// <summary>Wartet auf die laufenden Hintergrund-Meldungen (Tests, Herunterfahren).</summary>
    public Task WhenNotificationsCompleteAsync() => Task.WhenAll(_backgroundNotifications.Keys);

    private async Task NotifyAsync(Func<Task> notify, string what, string id)
    {
        try
        {
            await notify();
        }
        catch (Exception ex)
        {
            // Eine Meldung nach außen darf nie eine Entscheidung zurückdrehen.
            logger?.LogWarning(ex, "Absence {Id}: notification '{What}' failed", id, what);
        }
    }

    private static bool IsKimaiFailure(Exception exception, CancellationToken cancellationToken)
        => exception is KimaiApiException or HttpRequestException or JsonException or InvalidOperationException
            || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested);

    private static bool IsUnreachable(Exception exception)
        => exception is HttpRequestException or TaskCanceledException or KimaiApiException { IsTransient: true };

    private static string Describe(Exception exception) => exception switch
    {
        KimaiApiException api => api.Message,
        HttpRequestException or TaskCanceledException => "Kimai ist nicht erreichbar.",
        _ => exception.Message,
    };

    private static CorrectionResult<T> KimaiUnavailable<T>(Exception? exception = null) => exception is KimaiApiException { IsTransient: false }
        ? CorrectionResult<T>.Fail(CorrectionOutcome.Invalid, "Kimai hat die Anfrage abgelehnt.")
        : CorrectionResult<T>.Fail(CorrectionOutcome.Unavailable, "Kimai ist gerade nicht erreichbar. Bitte später erneut versuchen.");

    private static CorrectionResult<T> Unauthorized<T>() => CorrectionResult<T>.Fail(CorrectionOutcome.Unauthorized, "Nicht angemeldet.");

    private static CorrectionResult<T> NotFound<T>() => CorrectionResult<T>.Fail(CorrectionOutcome.NotFound, "Antrag nicht gefunden.");

    private static CorrectionResult<T> Invalid<T>(string message) => CorrectionResult<T>.Fail(CorrectionOutcome.Invalid, message);

    private static CorrectionResult<AbsenceDto> Current(AbsenceRequest request)
        => CorrectionResult<AbsenceDto>.Ok(ToDto(request, forEmployee: false));

    private static string? Truncate(string? value, int length) => value is { } v && v.Length > length ? v[..length] : value;

    private static string Format(DateOnly day) => day.ToString(DayFormat, CultureInfo.InvariantCulture);

    private static bool TryParseKind(string? value, out AbsenceKind kind)
    {
        // Nur die benannten Arten, keine Zahlen: Enum.TryParse nähme "7".
        var name = Enum.GetNames<AbsenceKind>()
            .FirstOrDefault(candidate => string.Equals(candidate, value?.Trim(), StringComparison.OrdinalIgnoreCase));
        kind = name is null ? default : Enum.Parse<AbsenceKind>(name);
        return name is not null;
    }

    /// <summary>Sortiert und eindeutig; false bei einem ungültigen Datum. Leer bleibt leer (meldet der Validator).</summary>
    private static bool TryParseDays(IReadOnlyList<string>? texts, out IReadOnlyList<DateOnly> days)
    {
        var parsed = new SortedSet<DateOnly>();
        foreach (var text in texts ?? [])
        {
            if (!DateOnly.TryParseExact(text?.Trim(), DayFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            {
                days = [];
                return false;
            }
            parsed.Add(day);
        }
        days = [.. parsed];
        return true;
    }

    /// <summary>
    /// EmployeeSettings ist eine Klasse mit init-Eigenschaften: über JSON kopiert,
    /// damit kein (auch künftiges) Feld beim Speichern verloren geht.
    /// </summary>
    private static EmployeeSettings WithVacationYears(EmployeeSettings employee, EmployeeVacationYear[] years)
    {
        var node = JsonSerializer.SerializeToNode(employee, SettingsJson)!.AsObject();
        node["vacationYears"] = JsonSerializer.SerializeToNode(years, SettingsJson);
        return node.Deserialize<EmployeeSettings>(SettingsJson)!;
    }

    private static AdminVacationBalanceDto ToBalanceDto(EmployeeSettings employee, int year, IEnumerable<AbsenceRequest> requests)
        => new(
            employee.Id,
            employee.DisplayName,
            employee.PersonnelNumber,
            employee.IsEnabled,
            employee.VacationDaysPerYear,
            employee.VacationYears.FirstOrDefault(candidate => candidate.Year == year)?.Note,
            VacationEntitlementCalculator.Calculate(employee, year, requests));

    private static AbsenceDto ToDto(AbsenceRequest request, bool forEmployee)
        => new(
            request.Id,
            request.EmployeeId,
            request.EmployeeName,
            request.Kind,
            request.Status,
            request.Days.Select(Format).ToArray(),
            request.Source,
            request.CreatedAt,
            request.CreatedBy,
            request.Comment,
            request.DecidedAt,
            request.DecidedBy,
            request.DecisionNote,
            // Der Mitarbeiter sieht keine technischen Kimai-Meldungen; ein
            // unvollständiges Storno betrifft nur den Admin.
            !forEmployee ? request.Error
                : request.Status == AbsenceStatus.Failed ? "Die Abwesenheit konnte nicht in Kimai eingetragen werden. Der Chef kümmert sich darum."
                : null,
            request.AppliedDays.Keys.Order(StringComparer.Ordinal).ToArray(),
            request.CancelledAt,
            request.CancelledBy);
}
