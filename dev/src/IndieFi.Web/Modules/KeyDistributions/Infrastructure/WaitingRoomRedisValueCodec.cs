using System.Globalization;
using IndieFi.Web.Modules.KeyDistributions.Domain;
using StackExchange.Redis;

namespace IndieFi.Web.Modules.KeyDistributions.Infrastructure;

public static class WaitingRoomRedisValueCodec
{
    private const int CurrentVersion = 1;
    private const char Separator = '|';

    public static RedisValue Serialize(WaitingRoomStoredEntry entry)
    {
        entry.Validate();
        return string.Join(
            Separator,
            CurrentVersion.ToString(CultureInfo.InvariantCulture),
            ((int)entry.State).ToString(CultureInfo.InvariantCulture),
            entry.Order.ToString(CultureInfo.InvariantCulture),
            entry.LastSeenUnixTimeMilliseconds.ToString(CultureInfo.InvariantCulture),
            entry.AdmissionExpiresUnixTimeMilliseconds?.ToString(
                CultureInfo.InvariantCulture) ?? string.Empty);
    }

    public static WaitingRoomStoredEntry Deserialize(RedisValue value)
    {
        if (value.IsNull)
        {
            throw new FormatException("A waiting-room entry cannot be null.");
        }

        var parts = value.ToString().Split(Separator, StringSplitOptions.None);
        if (parts.Length != 5
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            || version != CurrentVersion
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var stateValue)
            || !Enum.IsDefined(typeof(WaitingRoomEntryState), stateValue)
            || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var order)
            || !long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var lastSeen))
        {
            throw new FormatException("The waiting-room entry has an invalid format.");
        }

        long? admissionExpires = null;
        if (parts[4].Length > 0)
        {
            if (!long.TryParse(
                    parts[4],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var parsedAdmissionExpires))
            {
                throw new FormatException(
                    "The waiting-room admission expiry has an invalid format.");
            }

            admissionExpires = parsedAdmissionExpires;
        }

        try
        {
            return new WaitingRoomStoredEntry(
                (WaitingRoomEntryState)stateValue,
                order,
                lastSeen,
                admissionExpires).Validate();
        }
        catch (ArgumentException exception)
        {
            throw new FormatException(
                "The waiting-room entry violates its state rules.",
                exception);
        }
    }
}
