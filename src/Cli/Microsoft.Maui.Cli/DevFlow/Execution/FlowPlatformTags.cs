namespace Microsoft.Maui.Cli.DevFlow.Execution;

internal static class FlowPlatformTags
{
    private static readonly char[] Separators = [',', ';', '|'];

    public static IReadOnlyList<string> Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        var trimmed = value.Trim().Trim('[', ']');
        var tags = trimmed
            .Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Normalize)
            .Where(static tag => tag.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return tags.Length > 0 ? tags : [Normalize(trimmed)];
    }

    public static bool Matches(string? value, IReadOnlyCollection<string> aliases)
    {
        ArgumentNullException.ThrowIfNull(aliases);
        var normalizedAliases = aliases
            .Select(Normalize)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Parse(value).Any(normalizedAliases.Contains);
    }

    public static IReadOnlyList<string> Parse(IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return values
            .SelectMany(Parse)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string Normalize(string value)
    {
        var normalized = value.Trim().Trim('"', '\'').ToLowerInvariant();
        while (normalized.Contains("  ", StringComparison.Ordinal))
            normalized = normalized.Replace("  ", " ", StringComparison.Ordinal);
        return normalized.Replace('_', '-').Replace(' ', '-');
    }
}
