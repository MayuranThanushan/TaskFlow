using TaskFlow.Domain.Entities;
namespace TaskFlow.Infrastructure.Notifications;
public static class WatcherTiming
{
    public static DateTime ReadyAt(DateTime now, NotificationPreference preference)
    {
        var zone=NotificationSchedule.GetZone(preference.TimeZoneId);
        DateTime AfterQuiet(DateTime value)
        {
            if(preference.QuietStartHour is not {} start || preference.QuietEndHour is not {} end)return value;
            var local=TimeZoneInfo.ConvertTimeFromUtc(value,zone);var hour=local.Hour;
            if(!(start<end?hour>=start&&hour<end:hour>=start||hour<end))return value;
            var date=DateOnly.FromDateTime(local);if(start>end&&hour>=start)date=date.AddDays(1);
            return NotificationSchedule.DigestAtUtc(date,end,zone);
        }
        if(preference.WatcherDeliveryMode!="Digest")return AfterQuiet(now);
        var localDate=DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(now,zone));
        // Shift the digest slot first. Otherwise a slot hidden by quiet hours could
        // be postponed every day and never delivered at the end of quiet hours.
        var slots=Enumerable.Range(-1,3).Select(offset=>AfterQuiet(NotificationSchedule.DigestAtUtc(localDate.AddDays(offset),preference.DigestHour,zone))).Order().ToArray();
        if(slots.Any(at=>now>=at&&now<at.AddHours(1)))return AfterQuiet(now);
        return slots.First(at=>at>now);
    }
}
