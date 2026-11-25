using AoiUploader.Models;

namespace AoiUploader.Services;

/// <summary>Checks whether a file belongs to the value entered by the operator (Recipe / Work Order...).</summary>
public static class InputMatcher
{
    /// <summary>True if the file must be read before it can be matched.</summary>
    public static bool NeedsParse(InputSettings input) => input.MatchSource != MatchSource.FileNamePrefix;

    public static string FileNamePrefix(string path, string separator)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(separator)) return name;
        int i = name.IndexOf(separator, StringComparison.Ordinal);
        return i > 0 ? name[..i] : name;
    }

    /// <summary>The value(s) found in the file. Parsed file is required unless MatchSource = FileNamePrefix.</summary>
    public static List<string> GetFileValues(string path, ParsedFile? pf, InputSettings input)
    {
        switch (input.MatchSource)
        {
            case MatchSource.FileNamePrefix:
                return new() { FileNamePrefix(path, input.FileNameSeparator) };
            case MatchSource.RecipeLine:
                return pf == null ? new() : new() { pf.Recipe.Trim() };
            default:
                if (pf == null) return new();
                return pf.RawRecords
                    .Select(r => r.TryGetValue(input.MatchField.Trim(), out var v) ? v?.Trim() : null)
                    .Where(v => !string.IsNullOrEmpty(v))
                    .Select(v => v!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
        }
    }

    /// <returns>null if the file matches, otherwise a message for the operator.</returns>
    public static string? Check(List<string> fileValues, string entered, InputSettings input)
    {
        entered = entered.Trim();
        var label = string.IsNullOrWhiteSpace(input.Label) ? "Value" : input.Label;
        if (fileValues.Count == 0)
            return $"{label} not found in file";
        if (fileValues.Count == 1 && fileValues[0].Equals(entered, StringComparison.OrdinalIgnoreCase))
            return null;
        if (fileValues.Count > 1)
            return $"File has mixed {label} values: {string.Join(", ", fileValues)} (entered: {entered})";
        return $"File {label} is '{fileValues[0]}' but entered {label} is '{entered}'";
    }
}
