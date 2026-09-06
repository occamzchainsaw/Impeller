using Impeller.App.ViewModels.Notifications;
using Impeller.App.ViewModels.Shell;
using Impeller.App.ViewModels.Updates;
using Microsoft.Extensions.Logging;

namespace Impeller.App.Shell;

/// <summary>
/// Asks once, quietly, whether there is a newer release.
/// </summary>
/// <remarks>
/// <para>
/// Notifies and stops there. It does not download anything and it does not launch anything: the
/// engine half of an update needs administrator, so an app running as the user cannot finish the
/// job anyway, and a half-automatic update is worse than an honest link.
/// </para>
/// <para>
/// Once per run, well after startup, and never on the path to the window appearing. An update check
/// is the least urgent thing this program does and must never be the reason it is slow to open.
/// </para>
/// </remarks>
internal static partial class UpdateWatch
{
    /// <summary>Where releases are published.</summary>
    /// <remarks>
    /// Until this repository is public the request 404s, which the feed reports as "no information"
    /// like any other failure. That is the intended behaviour rather than a placeholder to remember:
    /// nothing here needs changing on the day it is published.
    /// </remarks>
    private const string Owner = "occamzchainsaw";
    private const string Repository = "impeller";

    /// <summary>
    /// Long enough to be out of the way.
    /// </summary>
    /// <remarks>
    /// The first seconds after launch belong to connecting to the engine and drawing the window.
    /// Nothing about this competes with that.
    /// </remarks>
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(20);

    private static partial class Log
    {
        [LoggerMessage(EventId = 53, Level = LogLevel.Information, Message = "Version {Version} is available.")]
        public static partial void Available(ILogger logger, string version);
    }

    /// <summary>Creates the feed the Settings page and the startup check share.</summary>
    public static IReleaseFeed Feed() => new GitHubReleaseFeed(Owner, Repository);

    /// <summary>
    /// Starts the background check, if the user has left it on.
    /// </summary>
    /// <remarks>
    /// Deliberately not awaited by anything and unable to fault the caller: a failed update check
    /// must be indistinguishable from no update check.
    /// </remarks>
    public static void Start(
        IReleaseFeed feed,
        ShellSettingsStore settings,
        NotificationCenter notifications,
        string version,
        ILogger logger)
    {
        if (!settings.Read().CheckForUpdates)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Delay).ConfigureAwait(false);

                if (await feed.LatestAsync().ConfigureAwait(false) is not { } latest
                    || !UpdateCheck.IsNewer(version, latest.Version))
                {
                    return;
                }

                Log.Available(logger, latest.Version);

                notifications.Inform(
                    $"Impeller {latest.Version} is available.",
                    $"You are running {version}. Settings has the link, and the installer updates "
                        + "the engine and the window together.");
            }
            catch (Exception)
            {
                // Nothing here is worth surfacing. The feed swallows its own failures already; this
                // is the belt for anything it does not, including the notification centre itself.
            }
        });
    }
}
