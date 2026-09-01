namespace Impeller.Core.Abstractions.Configuration;

/// <summary>How badly wrong something in a configuration is.</summary>
public enum ConfigurationSeverity
{
    /// <summary>Worth telling the user about; the configuration still loads and runs.</summary>
    Warning = 0,

    /// <summary>The configuration cannot be applied at all.</summary>
    Error,
}

/// <summary>Something noticed while checking a configuration.</summary>
/// <param name="Severity">Whether this stops the configuration being applied.</param>
/// <param name="Code">A stable identifier, so the UI can special-case an issue without parsing prose.</param>
/// <param name="Message">What to tell the user.</param>
public readonly record struct ConfigurationIssue(
    ConfigurationSeverity Severity,
    string Code,
    string Message);

/// <summary>
/// What checking a configuration turned up.
/// </summary>
/// <remarks>
/// Lives with the configuration vocabulary rather than with the validator, because the shell has to
/// receive one of these over the wire and render it. Validating an edit locally and sending it to
/// the engine should produce the same answer twice, and that only holds if both ends speak in the
/// same type.
/// </remarks>
/// <param name="Issues">Everything noticed, errors and warnings together, in the order found.</param>
public readonly record struct ConfigurationValidation(EquatableArray<ConfigurationIssue> Issues)
{
    /// <summary>Whether anything found prevents the configuration being applied.</summary>
    public bool HasErrors => Issues.Any(issue => issue.Severity == ConfigurationSeverity.Error);

    /// <summary>Just the blocking issues.</summary>
    public IEnumerable<ConfigurationIssue> Errors =>
        Issues.Where(issue => issue.Severity == ConfigurationSeverity.Error);

    /// <summary>Just the advisory ones.</summary>
    public IEnumerable<ConfigurationIssue> Warnings =>
        Issues.Where(issue => issue.Severity == ConfigurationSeverity.Warning);

    /// <summary>A clean result.</summary>
    public static ConfigurationValidation Clean => new([]);
}
