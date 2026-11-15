using TaskFlow.Infrastructure.Notifications;
using TaskFlow.Application.Features.Tasks;
namespace TaskFlow.Tests;
public sealed class CommentMentionsTests
{
    [Fact] public void MentionsAreExplicitOpaqueIdsAndNeverEmailOrHtmlResolution()
    {
        Assert.Equal(new long[] { 9007199254741201, 12 }, CommentMentions.Parse("@[9007199254741201] @[12] @[12] @email@example.test <script>@someone</script>"));
        Assert.Empty(CommentMentions.Parse("@name @[] @[12345678901234567890]"));
    }
    [Theory] [InlineData("@[0]")] [InlineData("@[01]")] [InlineData("@[9223372036854775808]")]
    public void InvalidRecognizedTokensAreRejected(string text) => Assert.Throws<TaskValidationException>(() => CommentMentions.Parse(text));
    [Fact] public void MentionFanoutAndTextAreBounded()
    {
        Assert.Throws<TaskValidationException>(() => CommentMentions.Parse(string.Join(' ', Enumerable.Range(1,11).Select(i => "@[" + i + "]"))));
        Assert.Throws<TaskValidationException>(() => CommentMentions.Parse(new string('x', 5001)));
    }
}
