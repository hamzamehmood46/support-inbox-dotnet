namespace Triage.Engine;

/// <summary>A clock that only moves when told to, for demos and tests.</summary>
public sealed class SimulatedClock : TimeProvider
{
    private DateTimeOffset _now;

    public SimulatedClock(DateTimeOffset start) => _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>
/// The hours a support team actually works (UTC). An SLA of "4 business hours" does not run overnight, at the weekend
/// or on a holiday, so every deadline is computed by walking through working time only.
/// </summary>
public sealed class BusinessCalendar
{
    private readonly HashSet<DayOfWeek> _days;
    private readonly HashSet<DateOnly> _holidays;

    public TimeSpan Opens { get; }
    public TimeSpan Closes { get; }

    public BusinessCalendar(TimeSpan opens, TimeSpan closes, IEnumerable<DayOfWeek> workDays, IEnumerable<DateOnly>? holidays = null)
    {
        if (opens < TimeSpan.Zero || closes > TimeSpan.FromHours(24) || opens >= closes)
        {
            throw new ArgumentException("Opening time must be before closing time, within one day.");
        }

        _days = [.. workDays];
        if (_days.Count == 0)
        {
            throw new ArgumentException("At least one working day is needed.");
        }

        _holidays = [.. holidays ?? []];
        Opens = opens;
        Closes = closes;
    }

    public static BusinessCalendar Standard(IEnumerable<DateOnly>? holidays = null) =>
        new(TimeSpan.FromHours(9), TimeSpan.FromHours(17), [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday], holidays);

    public bool IsWorkday(DateOnly date) => _days.Contains(date.DayOfWeek) && !_holidays.Contains(date);

    public bool IsWorkingTime(DateTimeOffset at)
    {
        var utc = at.ToUniversalTime();
        var date = DateOnly.FromDateTime(utc.UtcDateTime);
        return IsWorkday(date) && utc >= At(date, Opens) && utc < At(date, Closes);
    }

    /// <summary>The moment itself if the team is working, otherwise the next time it opens.</summary>
    public DateTimeOffset NextWorkingStart(DateTimeOffset from)
    {
        var cursor = from.ToUniversalTime();
        for (var i = 0; i < 3700; i++)
        {
            var date = DateOnly.FromDateTime(cursor.UtcDateTime);
            if (IsWorkday(date))
            {
                var open = At(date, Opens);
                var close = At(date, Closes);
                if (cursor < open)
                {
                    return open;
                }

                if (cursor < close)
                {
                    return cursor;
                }
            }

            cursor = At(date.AddDays(1), TimeSpan.Zero);
        }

        throw new InvalidOperationException("No working time found within ten years.");
    }

    /// <summary>The moment when <paramref name="duration"/> of working time has passed since <paramref name="start"/>.</summary>
    public DateTimeOffset Add(DateTimeOffset start, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return start;
        }

        var cursor = NextWorkingStart(start);
        var remaining = duration;
        while (true)
        {
            var close = At(DateOnly.FromDateTime(cursor.UtcDateTime), Closes);
            var available = close - cursor;
            if (remaining <= available)
            {
                return cursor + remaining;
            }

            remaining -= available;
            cursor = NextWorkingStart(close);
        }
    }

    /// <summary>The working time between two moments. Zero if the second is not after the first.</summary>
    public TimeSpan Between(DateTimeOffset from, DateTimeOffset to)
    {
        var a = from.ToUniversalTime();
        var b = to.ToUniversalTime();
        if (b <= a)
        {
            return TimeSpan.Zero;
        }

        var total = TimeSpan.Zero;
        for (var date = DateOnly.FromDateTime(a.UtcDateTime); date <= DateOnly.FromDateTime(b.UtcDateTime); date = date.AddDays(1))
        {
            if (!IsWorkday(date))
            {
                continue;
            }

            var start = Max(a, At(date, Opens));
            var end = Min(b, At(date, Closes));
            if (end > start)
            {
                total += end - start;
            }
        }

        return total;
    }

    private static DateTimeOffset At(DateOnly date, TimeSpan time) => new DateTimeOffset(date.Year, date.Month, date.Day, 0, 0, 0, TimeSpan.Zero) + time;

    private static DateTimeOffset Max(DateTimeOffset x, DateTimeOffset y) => x > y ? x : y;

    private static DateTimeOffset Min(DateTimeOffset x, DateTimeOffset y) => x < y ? x : y;
}
