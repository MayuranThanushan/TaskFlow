namespace TaskFlow.Domain.Entities;

public static class TextRules
{
    public static string Required(string value, int maximum, string field)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized) || normalized.Length > maximum)
            throw new ArgumentException($"{field} must contain 1 to {maximum} characters.", field);
        return normalized;
    }

    public static string? Optional(string? value, int maximum, string field)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized)) return null;
        if (normalized.Length > maximum)
            throw new ArgumentException($"{field} exceeds {maximum} characters.", field);
        return normalized;
    }
}
