using TaskFlow.Domain.Entities;
using TaskFlow.Infrastructure.Notifications;
namespace TaskFlow.Tests;
public sealed class WatcherTimingTests
{
    [Theory]
    [InlineData("Asia/Colombo","Immediate",9,22,7,"2026-11-15T18:00:00Z","2026-11-16T01:30:00Z")]
    [InlineData("Asia/Colombo","Immediate",9,9,17,"2026-11-15T04:30:00Z","2026-11-15T11:30:00Z")]
    [InlineData("America/New_York","Immediate",9,0,1,"2026-11-01T04:30:00Z","2026-11-01T05:00:00Z")]
    [InlineData("America/New_York","Digest",2,22,3,"2026-03-08T06:00:00Z","2026-03-08T07:00:00Z")]
    [InlineData("UTC","Digest",9,8,10,"2026-11-15T09:30:00Z","2026-11-15T10:00:00Z")]
    [InlineData("UTC","Digest",9,8,10,"2026-11-15T10:15:00Z","2026-11-15T10:15:00Z")]
    [InlineData("UTC","Digest",9,8,10,"2026-11-15T11:01:00Z","2026-11-16T10:00:00Z")]
    [InlineData("UTC","Digest",23,23,22,"2026-11-15T21:00:00Z","2026-11-15T22:00:00Z")]
    public void QuietHoursAndShiftedDigestSlotsHandleOvernightAndDst(string zone,string mode,int hour,int start,int end,string current,string expected)
    {
        var preference=new NotificationPreference{TimeZoneId=zone,WatcherDeliveryMode=mode,DigestHour=hour,QuietStartHour=start,QuietEndHour=end,DailyDigestEnabled=true};
        static DateTime At(string s)=>DateTimeOffset.Parse(s,System.Globalization.CultureInfo.InvariantCulture).UtcDateTime;
        Assert.Equal(At(expected),WatcherTiming.ReadyAt(At(current),preference));
    }
}
