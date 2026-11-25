namespace Hmrc.Rti.Api.Application.Ssp;

/// <summary>
/// A run of consecutive calendar days of sickness, both ends inclusive. This is
/// the raw input; whether it counts as a period of incapacity for work is
/// decided later, against <see cref="SspRules.MinimumPiwDays"/>.
/// </summary>
public sealed record SicknessPeriod
{
    public SicknessPeriod(DateOnly start, DateOnly end)
    {
        if (end < start)
        {
            throw new ArgumentException(
                $"Sickness ends ({end:yyyy-MM-dd}) before it starts ({start:yyyy-MM-dd}).", nameof(end));
        }

        Start = start;
        End = end;
    }

    public DateOnly Start { get; }

    public DateOnly End { get; }

    /// <summary>Calendar days covered, counting both the first and the last.</summary>
    public int CalendarDays => End.DayNumber - Start.DayNumber + 1;
}
