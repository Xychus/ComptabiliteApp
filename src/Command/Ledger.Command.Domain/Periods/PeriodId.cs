using System.Globalization;
using Ledger.SharedKernel;

namespace Ledger.Command.Domain.Periods;

/// <summary>Période comptable mensuelle (<c>AAAA-MM</c>) — ADR 0008, H4.</summary>
public readonly record struct PeriodId
{
    public PeriodId(int year, int month)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(year, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(year, 9999);
        ArgumentOutOfRangeException.ThrowIfLessThan(month, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(month, 12);
        Year = year;
        Month = month;
    }

    public int Year { get; }

    public int Month { get; }

    /// <summary>Période à laquelle appartient une date d'affaires.</summary>
    public static PeriodId FromBusinessDate(DateOnly businessDate) => new(businessDate.Year, businessDate.Month);

    public static Result<PeriodId> Parse(string value) =>
        DateOnly.TryParseExact(value, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? Result.Success(FromBusinessDate(date))
            : Result.Failure<PeriodId>(new DomainError("InvalidPeriod", $"Période invalide : « {value} » (format attendu AAAA-MM)."));

    public override string ToString() => $"{Year:D4}-{Month:D2}";
}
