using Impeller.Core.Abstractions.Configuration;

namespace Impeller.Core.Persistence.Legacy;

/// <summary>What became of one thing in the file being imported.</summary>
public enum ImportOutcome
{
    /// <summary>Carried across unchanged.</summary>
    Imported = 0,

    /// <summary>Carried across, but not identically. The note says what differs.</summary>
    Adjusted,

    /// <summary>Kept, but pointing at something that could not be resolved on this machine.</summary>
    Unresolved,

    /// <summary>Not carried across at all. The note says why.</summary>
    Skipped,
}

/// <summary>One line of the import report.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Subject">What it happened to, named the way the user named it.</param>
/// <param name="Message">What to tell them about it.</param>
public readonly record struct ImportNote(ImportOutcome Outcome, string Subject, string Message);

/// <summary>
/// A configuration recovered from a legacy file, and an account of what that cost.
/// </summary>
/// <remarks>
/// The account is not optional decoration. An import that silently drops a curve, or resolves a
/// sensor to the wrong fan, is worse than one that refuses — the fans quietly do the wrong thing and
/// the user has no reason to look. Everything this importer could not do exactly is written down
/// here and surfaced.
/// </remarks>
public sealed record ImportResult
{
    /// <summary>The configuration, ready to validate and apply.</summary>
    public ImpellerConfiguration Configuration { get; init; } = new();

    /// <summary>Everything worth saying about the import, in the order it was found.</summary>
    public EquatableArray<ImportNote> Notes { get; init; } = [];

    /// <summary>References that resolved to no sensor on this machine.</summary>
    public IEnumerable<ImportNote> Unresolved =>
        Notes.Where(note => note.Outcome == ImportOutcome.Unresolved);

    /// <summary>Things that could not be carried across.</summary>
    public IEnumerable<ImportNote> Skipped =>
        Notes.Where(note => note.Outcome == ImportOutcome.Skipped);

    /// <summary>Things that arrived, but changed on the way.</summary>
    public IEnumerable<ImportNote> Adjusted =>
        Notes.Where(note => note.Outcome == ImportOutcome.Adjusted);

    /// <summary>Whether anything needs the user's attention before this configuration is right.</summary>
    public bool NeedsAttention =>
        Notes.Any(note => note.Outcome is ImportOutcome.Unresolved or ImportOutcome.Skipped);
}

/// <summary>Thrown when a file cannot be read as a legacy configuration at all.</summary>
/// <remarks>
/// Distinct from anything in the report: the report describes an import that happened imperfectly,
/// this describes one that could not begin. A truncated file, a JSON syntax error, or a document
/// with no recognisable configuration section all land here rather than surfacing as whatever
/// exception the parser happened to throw.
/// </remarks>
public sealed class LegacyImportException : Exception
{
    public LegacyImportException()
        : base("The file could not be read as a FanControl configuration.")
    {
    }

    public LegacyImportException(string message)
        : base(message)
    {
    }

    public LegacyImportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
