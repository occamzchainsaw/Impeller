namespace Impeller.Core.Abstractions.Configuration;

/// <summary>What became of one thing in a file being imported.</summary>
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

/// <summary>One line of an import report.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Subject">What it happened to, named the way the user named it.</param>
/// <param name="Message">What to tell them about it.</param>
/// <remarks>
/// Here rather than beside the importer because both ends of the channel render these: the engine
/// produces them and the shell shows them. Keeping the vocabulary in the layer both already
/// reference is what stops the wire format needing its own near-identical copy.
/// </remarks>
public readonly record struct ImportNote(ImportOutcome Outcome, string Subject, string Message);
