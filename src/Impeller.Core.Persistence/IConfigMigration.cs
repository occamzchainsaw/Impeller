using System.Text.Json.Nodes;

namespace Impeller.Core.Persistence;

/// <summary>
/// A single, self-contained upgrade of a configuration document from one schema version
/// to the next.
/// </summary>
/// <remarks>
/// <para>
/// Migrations operate on the parsed document tree, never on its text. The app being replaced
/// learned this the hard way: its early migrations rewrote serialized JSON with line-based
/// regexes, which are fragile against any formatting change, and only its last migration used a
/// document tree. Starting from the tree makes every migration order-independent of formatting
/// and testable against a literal document.
/// </para>
/// <para>
/// Each migration advances exactly one version. Chains are composed by the runner, so a document
/// three versions behind runs three migrations in order rather than one migration that has to
/// know about every historical shape.
/// </para>
/// </remarks>
public interface IConfigMigration
{
    /// <summary>
    /// The version this migration upgrades from. It produces <c>FromVersion + 1</c>.
    /// </summary>
    int FromVersion { get; }

    /// <summary>
    /// What this migration does, in a few words, for the log written when it runs.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// Rewrites the document in place.
    /// </summary>
    /// <param name="document">
    /// The whole configuration document, including its version property. Implementations should
    /// tolerate absent or unexpected nodes and leave them alone rather than throwing: a document
    /// that has already been partly hand-edited is still worth recovering.
    /// </param>
    void Apply(JsonObject document);
}
