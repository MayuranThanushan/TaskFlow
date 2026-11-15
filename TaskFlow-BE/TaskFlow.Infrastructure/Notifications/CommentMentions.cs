using System.Globalization;
using System.Text.RegularExpressions;
using TaskFlow.Application.Features.Tasks;
namespace TaskFlow.Infrastructure.Notifications;
public static partial class CommentMentions
{
    [GeneratedRegex(@"@\[([0-9]{1,19})\]", RegexOptions.CultureInvariant, 100)]
    private static partial Regex Tokens();
    public static long[] Parse(string body)
    {
        if (body.Length > 5000) throw new TaskValidationException("body", "Use at most 5000 characters.");
        var ids = new HashSet<long>();
        foreach (Match match in Tokens().Matches(body))
        {
            var text = match.Groups[1].Value;
            if (text[0] == '0' || !long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
                throw new TaskValidationException("body", "Choose a current project collaborator for each mention.");
            ids.Add(id);
        }
        if (ids.Count > 10) throw new TaskValidationException("body", "Mention at most 10 people per comment.");
        return ids.ToArray();
    }
}
