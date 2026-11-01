using System.Text.RegularExpressions;

namespace PrivacyLlm.Core.Pipeline;

/// <summary>
/// Boundary validation for identifiers that become database keys and audit filters. Restricting
/// them to a plain character set keeps every storage provider comparing them the same way
/// (SQL Server pads trailing spaces even under binary collations) and keeps them within column limits.
/// </summary>
public static partial class InputLimits
{
    public const int IdentifierMaxLength = 128;
    public const int ReferenceMaxLength = 256;

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:@\-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierRegex();

    public static bool IsValidIdentifier(string? value) => value is not null && IdentifierRegex().IsMatch(value);

    /// <summary>Returns problems keyed by field name; empty when the task is acceptable.</summary>
    public static Dictionary<string, string[]> Validate(LlmTask task)
    {
        var errors = new Dictionary<string, string[]>();
        Check(errors, nameof(task.TaskId), task.TaskId);
        Check(errors, nameof(task.TaskType), task.TaskType);
        Check(errors, nameof(task.Purpose), task.Purpose);
        if (task.DataPrincipalId is not null) Check(errors, nameof(task.DataPrincipalId), task.DataPrincipalId);
        return errors;
    }

    public static Dictionary<string, string[]> ValidateConsent(string dataPrincipalId, string purpose, string? consentManagerRef)
    {
        var errors = new Dictionary<string, string[]>();
        Check(errors, "DataPrincipalId", dataPrincipalId);
        Check(errors, "Purpose", purpose);
        if (consentManagerRef is { Length: > ReferenceMaxLength })
            errors["ConsentManagerRef"] = new[] { $"must be at most {ReferenceMaxLength} characters" };
        return errors;
    }

    private static void Check(Dictionary<string, string[]> errors, string field, string? value)
    {
        if (!IsValidIdentifier(value))
            errors[field] = new[] { $"must be 1-{IdentifierMaxLength} characters: letters, digits and . _ : @ -, starting with a letter or digit" };
    }
}
