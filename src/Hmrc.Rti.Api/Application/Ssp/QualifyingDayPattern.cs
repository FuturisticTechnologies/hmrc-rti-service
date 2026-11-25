// ---------------------------------------------------------------------------
// Qualifying days: the days of the week a worker is normally required to work,
// as agreed between employer and worker. SSP is paid only for qualifying days,
// and waiting days, periods of incapacity and the daily rate are all counted in
// them, so every later SSP step builds on this type.
// ---------------------------------------------------------------------------
namespace Hmrc.Rti.Api.Application.Ssp;

/// <summary>An immutable, non-empty set of weekdays on which SSP can be payable.</summary>
public sealed class QualifyingDayPattern : IEquatable<QualifyingDayPattern>
{
    private readonly HashSet<DayOfWeek> _days;

    public QualifyingDayPattern(IEnumerable<DayOfWeek> days)
    {
        ArgumentNullException.ThrowIfNull(days);
        _days = [.. days];

        // A pattern with no days would make every daily rate a division by zero.
        // HMRC's rule is that at least one qualifying day is always agreed.
        if (_days.Count == 0)
        {
            throw new ArgumentException("A qualifying day pattern needs at least one day.", nameof(days));
        }
    }

    /// <summary>Monday to Friday, the usual pattern.</summary>
    public static QualifyingDayPattern Weekdays { get; } = new(
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday]);

    /// <summary>How many qualifying days fall in one week. This is the divisor for the daily SSP rate.</summary>
    public int DaysPerWeek => _days.Count;

    public bool IsQualifying(DateOnly date) => _days.Contains(date.DayOfWeek);

    /// <summary>Qualifying days from <paramref name="start"/> to <paramref name="end"/>, both inclusive.</summary>
    public int CountBetween(DateOnly start, DateOnly end)
    {
        if (end < start)
        {
            throw new ArgumentException("End date is before start date.", nameof(end));
        }

        var count = 0;
        for (var date = start; date <= end; date = date.AddDays(1))
        {
            if (IsQualifying(date))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Each qualifying date from <paramref name="start"/> to <paramref name="end"/>, in order.</summary>
    public IEnumerable<DateOnly> QualifyingDatesBetween(DateOnly start, DateOnly end)
    {
        if (end < start)
        {
            throw new ArgumentException("End date is before start date.", nameof(end));
        }

        for (var date = start; date <= end; date = date.AddDays(1))
        {
            if (IsQualifying(date))
            {
                yield return date;
            }
        }
    }

    public bool Equals(QualifyingDayPattern? other) => other is not null && _days.SetEquals(other._days);

    public override bool Equals(object? obj) => Equals(obj as QualifyingDayPattern);

    public override int GetHashCode()
    {
        var hash = 0;
        foreach (var day in _days)
        {
            hash ^= (int)day; // order-independent, as the set is
        }

        return hash;
    }
}
