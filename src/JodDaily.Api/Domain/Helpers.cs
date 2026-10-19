using System.Globalization;

namespace JodDaily.Domain;

public static class BangkokTime
{
    private static readonly TimeZoneInfo Tz = Find();

    private static TimeZoneInfo Find()
    {
        foreach (var id in new[] { "Asia/Bangkok", "SE Asia Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch (TimeZoneNotFoundException) { }
        }
        return TimeZoneInfo.CreateCustomTimeZone("ICT", TimeSpan.FromHours(7), "ICT", "ICT");
    }

    public static DateTime Now => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Tz);
    public static DateOnly Today => DateOnly.FromDateTime(Now);
    /// <summary>Midnight (Bangkok) at the start of the given day, as UTC.</summary>
    public static DateTime StartOfDayUtc(DateOnly day) => TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue), Tz);
    public static DateTime FromUtc(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Tz);
}

public static class Money
{
    /// <summary>500 → "500", 9950 satang → "99.50"; thousands separated.</summary>
    public static string Format(long satang)
    {
        var baht = satang / 100m;
        return satang % 100 == 0
            ? baht.ToString("N0", CultureInfo.InvariantCulture)
            : baht.ToString("N2", CultureInfo.InvariantCulture);
    }

    public static long ToSatang(decimal baht) => (long)Math.Round(baht * 100m, MidpointRounding.AwayFromZero);
}
