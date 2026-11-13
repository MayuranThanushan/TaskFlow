using TaskFlow.Infrastructure.Notifications;
namespace TaskFlow.Tests;
public sealed class NotificationScheduleTests
{
    [Theory]
    [InlineData("Asia/Colombo", "2026-11-13", 9, "2026-11-13T03:30:00Z")]
    [InlineData("America/New_York", "2026-03-08", 2, "2026-03-08T07:00:00Z")]
    [InlineData("America/New_York", "2026-11-01", 1, "2026-11-01T05:00:00Z")]
    [InlineData("Australia/Lord_Howe", "2026-10-04", 2, "2026-10-03T15:30:00Z")]
    [InlineData("UTC", "2026-11-13", 0, "2026-11-13T00:00:00Z")]
    public void DigestUsesLocalDateAndResolvesDstDeterministically(string zone, string date, int hour, string expected)
    {
        var result = NotificationSchedule.DigestAtUtc(DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture), hour, NotificationSchedule.GetZone(zone));
        Assert.Equal(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture).UtcDateTime, result);
        Assert.Equal(DateTimeKind.Utc, result.Kind);
    }
    [Theory]
    [InlineData("")]
    [InlineData("Definitely/NotAZone")]
    [InlineData("Eastern Standard Time")]
    public void RejectsInvalidOrPlatformSpecificZones(string zone) => Assert.Throws<ArgumentException>(() => NotificationSchedule.GetZone(zone));
}
