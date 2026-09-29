using Stempeluhr.Api.Models;

namespace Stempeluhr.Api.Services;

/// <summary>
/// Failed-PIN backoff for the kiosk endpoints (issue #8). A 4-digit PIN has
/// 10,000 combinations, and an IP limit is only a global budget behind the
/// Cloudflare tunnel - so failures are counted per employee: after
/// <see cref="EmployeeThreshold"/> failures in a row the account is locked
/// for 1, then 5, then 15 minutes. A successful authentication resets it.
///
/// PIN login and hours carry no employee ID (the PIN alone selects the
/// employee); they share the global <see cref="PinLoginKey"/> with a higher
/// threshold, because every terminal contributes to it.
///
/// While locked, a request is refused BEFORE its PIN is checked - otherwise
/// the lock would still reveal the verdict. State lives in memory only and
/// covers configured employees plus the one global key, so it stays small.
/// </summary>
public sealed class PinAttemptGuard(ILogger<PinAttemptGuard>? logger = null, TimeProvider? clock = null)
{
    /// <summary>Global key for PIN checks without an employee ID.</summary>
    public const string PinLoginKey = "pin-login";

    public const int EmployeeThreshold = 5;
    public const int PinLoginThreshold = 10;

    private static readonly TimeSpan[] LockDurations =
        [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)];

    /// <summary>
    /// Without a failure for this long, count and escalation start over:
    /// a typo now and then must never add up to a lock.
    /// </summary>
    private static readonly TimeSpan ForgetAfter = TimeSpan.FromHours(1);

    private sealed record Entry(int Failures, int Locks, DateTimeOffset LastFailureAt, DateTimeOffset LockedUntil);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public static string EmployeeKey(string employeeId) => $"employee:{employeeId}";

    /// <summary>
    /// PIN or card check for a named employee, guarded by that employee's
    /// lock. Unknown or disabled IDs get no counter: there is no account to
    /// protect, and arbitrary IDs must not grow the table.
    /// </summary>
    /// <exception cref="PinLockedException">The employee is locked.</exception>
    public EmployeeSettings? Authenticate(RuntimeSettings settings, string? employeeId, Func<EmployeeSettings?> verify)
    {
        var known = settings.Employees.FirstOrDefault(candidate => candidate.IsEnabled
            && string.Equals(candidate.Id, employeeId, StringComparison.OrdinalIgnoreCase));
        return known is null ? verify() : Guard(EmployeeKey(known.Id), verify);
    }

    /// <summary>
    /// PIN login and hours carry no employee ID: they share one global lock
    /// (<see cref="PinLoginKey"/>). An empty PIN is no guess.
    /// </summary>
    /// <exception cref="PinLockedException">PIN login is locked.</exception>
    public EmployeeSettings? AuthenticatePinLogin(string? pin, Func<EmployeeSettings?> verify)
    {
        return string.IsNullOrWhiteSpace(pin) ? verify() : Guard(PinLoginKey, verify);
    }

    private EmployeeSettings? Guard(string key, Func<EmployeeSettings?> verify)
    {
        ThrowIfLocked(key);
        var employee = verify();
        if (employee is null)
        {
            RecordFailure(key);
        }
        else
        {
            RecordSuccess(key);
        }

        return employee;
    }

    /// <summary>Remaining lock time, or null when the key may try a PIN.</summary>
    public TimeSpan? RemainingLock(string key)
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            return _entries.TryGetValue(key, out var entry) && entry.LockedUntil > now
                ? entry.LockedUntil - now
                : null;
        }
    }

    /// <exception cref="PinLockedException">The key is locked.</exception>
    public void ThrowIfLocked(string key)
    {
        if (RemainingLock(key) is { } remaining)
        {
            throw new PinLockedException(remaining);
        }
    }

    public void RecordFailure(string key)
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            var entry = _entries.TryGetValue(key, out var existing) && now - existing.LastFailureAt < ForgetAfter
                ? existing
                : new Entry(0, 0, now, DateTimeOffset.MinValue);
            if (entry.LockedUntil > now)
            {
                // Callers check the lock first; a failure that raced past it
                // must not extend or escalate the lock.
                return;
            }

            var failures = entry.Failures + 1;
            var threshold = key == PinLoginKey ? PinLoginThreshold : EmployeeThreshold;
            if (failures < threshold)
            {
                _entries[key] = entry with { Failures = failures, LastFailureAt = now };
                return;
            }

            var duration = LockDurations[Math.Min(entry.Locks, LockDurations.Length - 1)];
            _entries[key] = new Entry(0, entry.Locks + 1, now, now + duration);
            logger?.LogWarning(
                "PIN locked for {Key} after {Failures} failed attempt(s) - {Minutes} minute(s)",
                key, failures, duration.TotalMinutes);
        }
    }

    public void RecordSuccess(string key)
    {
        lock (_gate)
        {
            _entries.Remove(key);
        }
    }
}

/// <summary>
/// Too many failed PIN attempts. Answered with 429 and Retry-After: the kiosk
/// treats that as transient - live stamps are queued, and a cached PIN is not
/// forgotten as it would be after a 401.
/// </summary>
public sealed class PinLockedException(TimeSpan retryAfter)
    : InvalidOperationException($"Zu viele falsche PIN-Eingaben - bitte in {Math.Max(1, (int)Math.Ceiling(retryAfter.TotalMinutes))} Min. erneut versuchen.")
{
    public TimeSpan RetryAfter { get; } = retryAfter;

    /// <summary>
    /// Endpoint filter for every PIN-protected endpoint. Not the global
    /// exception handler: that logs each request as an error, and a
    /// guessing run would flood the log with them.
    /// </summary>
    public static async ValueTask<object?> Filter(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (PinLockedException locked)
        {
            context.HttpContext.Response.Headers.RetryAfter =
                ((int)Math.Ceiling(locked.RetryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Results.Json(new { message = locked.Message }, statusCode: StatusCodes.Status429TooManyRequests);
        }
    }
}
