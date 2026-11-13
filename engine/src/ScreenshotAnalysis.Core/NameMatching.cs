namespace ScreenshotAnalysis;

public enum NameMatchingPolicy { OrdinalIgnoreCase, ZeroToOIgnoreCase }

/// <summary>Normalizes tags without changing source mentions or evidence.</summary>
public static class NameMatching
{
    public static string Normalize(string name, NameMatchingPolicy policy) => policy switch
    {
        NameMatchingPolicy.OrdinalIgnoreCase => name,
        NameMatchingPolicy.ZeroToOIgnoreCase => name.Replace('0', 'O'),
        _ => throw new ArgumentOutOfRangeException(nameof(policy))
    };

    public static string Version(NameMatchingPolicy policy) => policy switch
    {
        NameMatchingPolicy.OrdinalIgnoreCase => "ordinal-ignore-case",
        NameMatchingPolicy.ZeroToOIgnoreCase => "zero-to-o-ordinal-ignore-case-v1",
        _ => throw new ArgumentOutOfRangeException(nameof(policy))
    };

    public static NameMatchingPolicy Parse(string value) => value switch
    {
        "exact" => NameMatchingPolicy.OrdinalIgnoreCase,
        "zero-o" => NameMatchingPolicy.ZeroToOIgnoreCase,
        _ => throw new ArgumentException("Name matching must be exact or zero-o.")
    };
}
