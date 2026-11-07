using TaskFlow.Infrastructure.Identity;
namespace TaskFlow.Tests;

public class MfaCodesTests
{
    // Published RFC 6238 Appendix B SHA-1 test key; never a real credential.
    private const string ReferenceKey = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ"; // gitleaks:allow -- public RFC 6238 Appendix B test vector
    [Theory]
    [InlineData(59L, "94287082")]
    [InlineData(1111111109L, "07081804")]
    [InlineData(1111111111L, "14050471")]
    [InlineData(1234567890L, "89005924")]
    [InlineData(2000000000L, "69279037")]
    [InlineData(20000000000L, "65353130")]
    public void MatchesPublishedSha1Vectors(long seconds, string expected) => Assert.Equal(expected, MfaCodes.At(ReferenceKey, seconds / 30, 8));
    [Fact]
    public void RejectsReplayAndOutsideWindowAndMalformedCodes()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1234567890); var step = now.ToUnixTimeSeconds() / 30;
        var code = MfaCodes.At(ReferenceKey, step);
        Assert.Equal(step, MfaCodes.Match(ReferenceKey, code, now, step - 1));
        Assert.Null(MfaCodes.Match(ReferenceKey, code, now, step));
        Assert.Null(MfaCodes.Match(ReferenceKey, code, now.AddMinutes(2), -1));
        foreach (var malformed in new[] { "", "12345", "1234567", "１２３４５６", " 123456" }) Assert.Null(MfaCodes.Match(ReferenceKey, malformed, now, -1));
    }
}
