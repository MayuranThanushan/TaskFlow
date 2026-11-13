namespace TaskFlow.Infrastructure.Notifications;

// Wall-clock rules are explicit: skip a missing time forward; use the first occurrence
// of a repeated time. A local date is the digest identity, so DST cannot double it.
public static class NotificationSchedule
{
    public static TimeZoneInfo GetZone(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 100 || (id != "UTC" && !TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out _)))
            throw new ArgumentException("Choose an IANA timezone, for example Asia/Colombo.");
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { throw new ArgumentException("Unsupported timezone."); }
        catch (InvalidTimeZoneException) { throw new ArgumentException("Unsupported timezone."); }
    }
    public static DateTime DigestAtUtc(DateOnly date, int hour, TimeZoneInfo zone)
    {
        if (hour is < 0 or > 23) throw new ArgumentOutOfRangeException(nameof(hour));
        var local = date.ToDateTime(new TimeOnly(hour, 0), DateTimeKind.Unspecified);
        // Bounded even for historical full-day timezone jumps.
        for (var minutes = 0; zone.IsInvalidTime(local) && minutes < 1440; minutes++) local = local.AddMinutes(1);
        if (zone.IsInvalidTime(local)) throw new ArgumentException("Unsupported local date.");
        return zone.IsAmbiguousTime(local)
            ? DateTime.SpecifyKind(local - zone.GetAmbiguousTimeOffsets(local).Max(), DateTimeKind.Utc)
            : TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }
}
