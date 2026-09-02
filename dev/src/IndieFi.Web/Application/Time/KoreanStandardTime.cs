namespace IndieFi.Web.Application.Time;

public static class KoreanStandardTime
{
    public static readonly TimeSpan UtcOffset = TimeSpan.FromHours(9);

    public static DateTimeOffset FromLocal(DateTime localDateTime)
    {
        var unspecified = DateTime.SpecifyKind(
            localDateTime,
            DateTimeKind.Unspecified);
        return new DateTimeOffset(unspecified, UtcOffset);
    }

    public static DateTimeOffset ToLocal(DateTimeOffset instant) =>
        instant.ToOffset(UtcOffset);
}
