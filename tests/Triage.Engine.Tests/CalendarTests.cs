using Triage.Engine;

namespace Triage.Engine.Tests;

public class CalendarTests
{
    // October 2026: Mon 5, Tue 6, Wed 7, Thu 8, Fri 9, Sat 10, Sun 11, Mon 12.
    private static readonly BusinessCalendar Cal = BusinessCalendar.Standard();

    private static DateTimeOffset At(int day, int hour, int minute = 0) => Shop.At(10, day, hour, minute);

    [Theory]
    [InlineData(5, 9, 0, true)]
    [InlineData(5, 8, 59, false)]
    [InlineData(5, 16, 59, true)]
    [InlineData(5, 17, 0, false)]
    [InlineData(9, 12, 0, true)]
    [InlineData(10, 12, 0, false)]
    [InlineData(11, 12, 0, false)]
    [InlineData(12, 9, 0, true)]
    public void Working_time_is_nine_to_five_on_weekdays(int day, int hour, int minute, bool expected) =>
        Assert.Equal(expected, Cal.IsWorkingTime(At(day, hour, minute)));

    [Fact]
    public void A_holiday_is_not_working_time()
    {
        var cal = BusinessCalendar.Standard([new DateOnly(2026, 10, 12)]);

        Assert.False(cal.IsWorkingTime(At(12, 12)));
        Assert.True(cal.IsWorkingTime(At(13, 12)));
    }

    [Theory]
    [InlineData(5, 12, 0, 5, 12, 0)]
    [InlineData(5, 7, 30, 5, 9, 0)]
    [InlineData(5, 17, 0, 6, 9, 0)]
    [InlineData(5, 20, 0, 6, 9, 0)]
    [InlineData(9, 17, 30, 12, 9, 0)]
    [InlineData(10, 11, 0, 12, 9, 0)]
    [InlineData(11, 23, 59, 12, 9, 0)]
    public void The_next_working_start_is_now_if_open_otherwise_the_next_opening(int day, int hour, int minute, int expectedDay, int expectedHour, int expectedMinute) =>
        Assert.Equal(At(expectedDay, expectedHour, expectedMinute), Cal.NextWorkingStart(At(day, hour, minute)));

    [Fact]
    public void A_holiday_weekend_is_skipped_to_the_next_working_day()
    {
        var cal = BusinessCalendar.Standard([new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 13)]);

        Assert.Equal(At(14, 9), cal.NextWorkingStart(At(9, 18)));
    }

    [Theory]
    [InlineData(5, 10, 0, 2, 5, 12, 0)]
    [InlineData(5, 15, 0, 2, 5, 17, 0)]
    [InlineData(5, 16, 0, 2, 6, 10, 0)]
    [InlineData(9, 16, 0, 2, 12, 10, 0)]
    [InlineData(10, 12, 0, 1, 12, 10, 0)]
    [InlineData(5, 8, 0, 1, 5, 10, 0)]
    [InlineData(5, 10, 0, 24, 8, 10, 0)]
    [InlineData(5, 9, 0, 8, 5, 17, 0)]
    [InlineData(5, 9, 0, 16, 6, 17, 0)]
    [InlineData(9, 9, 0, 40, 15, 17, 0)]
    public void Adding_working_hours_skips_nights_weekends_and_lunch_breaks_of_the_calendar(int day, int hour, int minute, int hours, int expectedDay, int expectedHour, int expectedMinute) =>
        Assert.Equal(At(expectedDay, expectedHour, expectedMinute), Cal.Add(At(day, hour, minute), TimeSpan.FromHours(hours)));

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Adding_nothing_or_less_returns_the_start(int minutes)
    {
        var start = At(10, 12);

        Assert.Equal(start, Cal.Add(start, TimeSpan.FromMinutes(minutes)));
    }

    [Fact]
    public void Adding_across_a_holiday_skips_it()
    {
        var cal = BusinessCalendar.Standard([new DateOnly(2026, 10, 6)]);

        Assert.Equal(At(7, 10), cal.Add(At(5, 16), TimeSpan.FromHours(2)));
    }

    [Fact]
    public void Odd_amounts_of_time_are_kept_to_the_minute()
    {
        Assert.Equal(At(5, 11, 37), Cal.Add(At(5, 10), TimeSpan.FromMinutes(97)));
        Assert.Equal(At(6, 9, 10), Cal.Add(At(5, 16, 30), TimeSpan.FromMinutes(40)));
    }

    [Theory]
    [InlineData(5, 10, 0, 5, 12, 30, 150)]
    [InlineData(5, 16, 0, 6, 10, 0, 120)]
    [InlineData(9, 16, 0, 12, 10, 0, 120)]
    [InlineData(5, 18, 0, 5, 23, 0, 0)]
    [InlineData(10, 9, 0, 11, 17, 0, 0)]
    [InlineData(5, 7, 0, 5, 18, 0, 480)]
    [InlineData(5, 9, 0, 9, 17, 0, 2400)]
    [InlineData(5, 9, 0, 12, 17, 0, 2880)]
    public void The_working_time_between_two_moments_ignores_everything_outside_the_calendar(int d1, int h1, int m1, int d2, int h2, int m2, int expectedMinutes) =>
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), Cal.Between(At(d1, h1, m1), At(d2, h2, m2)));

    [Fact]
    public void Between_is_zero_when_the_end_is_not_after_the_start()
    {
        Assert.Equal(TimeSpan.Zero, Cal.Between(At(5, 12), At(5, 12)));
        Assert.Equal(TimeSpan.Zero, Cal.Between(At(6, 12), At(5, 12)));
    }

    [Fact]
    public void A_holiday_adds_no_working_time()
    {
        var cal = BusinessCalendar.Standard([new DateOnly(2026, 10, 6)]);

        Assert.Equal(TimeSpan.FromHours(8), cal.Between(At(5, 17), At(7, 17)));
    }

    [Theory]
    [InlineData(5, 10, 0, 7)]
    [InlineData(5, 16, 30, 3)]
    [InlineData(5, 7, 0, 40)]
    [InlineData(8, 14, 20, 63)]
    [InlineData(10, 11, 0, 5)]
    [InlineData(11, 9, 0, 25)]
    public void Between_and_add_are_inverses_for_any_start_time(int day, int hour, int minute, int hours)
    {
        var start = At(day, hour, minute);
        var duration = TimeSpan.FromHours(hours);

        var end = Cal.Add(start, duration);

        Assert.Equal(duration, Cal.Between(start, end));
        Assert.True(Cal.IsWorkingTime(end - TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void A_custom_calendar_can_work_other_hours_and_days()
    {
        var cal = new BusinessCalendar(TimeSpan.FromHours(8), TimeSpan.FromHours(20), [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Saturday]);

        Assert.Equal(At(10, 8), cal.NextWorkingStart(At(7, 12)));
        Assert.Equal(At(6, 9), cal.Add(At(5, 19), TimeSpan.FromHours(2)));
        Assert.Equal(TimeSpan.FromHours(12), cal.Between(At(10, 0), At(10, 23)));
    }

    [Theory]
    [InlineData(17, 9)]
    [InlineData(9, 9)]
    [InlineData(-1, 9)]
    [InlineData(9, 25)]
    public void A_calendar_needs_opening_before_closing_within_a_day(int open, int close)
    {
        Assert.Throws<ArgumentException>(() => new BusinessCalendar(TimeSpan.FromHours(open), TimeSpan.FromHours(close), [DayOfWeek.Monday]));
    }

    [Fact]
    public void A_calendar_needs_at_least_one_working_day() =>
        Assert.Throws<ArgumentException>(() => new BusinessCalendar(TimeSpan.FromHours(9), TimeSpan.FromHours(17), []));

    [Fact]
    public void Times_given_with_an_offset_are_treated_as_the_moment_they_are()
    {
        var local = new DateTimeOffset(2026, 10, 5, 14, 0, 0, TimeSpan.FromHours(5));

        Assert.True(Cal.IsWorkingTime(local));
        Assert.Equal(At(5, 9), Cal.NextWorkingStart(new DateTimeOffset(2026, 10, 5, 3, 0, 0, TimeSpan.FromHours(-2))));
    }
}

public class SlaPolicyTests
{
    [Fact]
    public void The_default_targets_get_looser_as_priority_drops()
    {
        var policy = SlaPolicy.Default;

        Assert.Equal(new SlaTarget(TimeSpan.FromHours(1), TimeSpan.FromHours(4)), policy.For(Priority.Urgent));
        Assert.Equal(new SlaTarget(TimeSpan.FromHours(4), TimeSpan.FromHours(8)), policy.For(Priority.High));
        Assert.Equal(new SlaTarget(TimeSpan.FromHours(8), TimeSpan.FromHours(24)), policy.For(Priority.Normal));
        Assert.Equal(new SlaTarget(TimeSpan.FromHours(24), TimeSpan.FromHours(40)), policy.For(Priority.Low));
    }

    [Fact]
    public void Every_priority_needs_a_target()
    {
        var missing = new Dictionary<Priority, SlaTarget> { [Priority.Low] = new(TimeSpan.FromHours(1), TimeSpan.FromHours(2)) };

        Assert.Throws<ArgumentException>(() => new SlaPolicy(missing));
    }

    [Fact]
    public void A_resolution_target_cannot_be_shorter_than_the_first_response_target()
    {
        var bad = Enum.GetValues<Priority>().ToDictionary(p => p, _ => new SlaTarget(TimeSpan.FromHours(4), TimeSpan.FromHours(2)));

        Assert.Throws<ArgumentException>(() => new SlaPolicy(bad));
    }

    [Fact]
    public void A_first_response_target_must_be_above_zero()
    {
        var bad = Enum.GetValues<Priority>().ToDictionary(p => p, _ => new SlaTarget(TimeSpan.Zero, TimeSpan.FromHours(2)));

        Assert.Throws<ArgumentException>(() => new SlaPolicy(bad));
    }
}
