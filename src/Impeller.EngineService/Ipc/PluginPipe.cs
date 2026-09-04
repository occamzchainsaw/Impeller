using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Impeller.Plugins.Abstractions;

namespace Impeller.EngineService.Ipc;

/// <summary>
/// Creates the listening end of the plugin pipe.
/// </summary>
/// <remarks>
/// <para>
/// A pipe of its own rather than the shell's, for a reason that is about people rather than
/// plumbing: the shell is a person driving the engine while a window is open, and a plugin is a
/// program asking for a standing permission it will hold across restarts. They deserve different
/// admission rules. It also means the engine can cut every plugin off — during a failsafe, or a
/// shutdown — without disturbing the window someone is looking at.
/// </para>
/// <para>
/// The access policy is <see cref="EnginePipe"/>'s, and the same reasoning applies: the engine runs
/// as LocalSystem, plugins run unelevated as the signed-in user, and the pipe has to cross that
/// gap. What differs is what happens after the connection is made — a shell is served immediately,
/// where a plugin gets nothing at all until the user has approved it.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class PluginPipe
{
    /// <summary>
    /// How many plugin connections the engine will hold open at once.
    /// </summary>
    /// <remarks>
    /// Comfortably above the number of plugins anyone will run and comfortably above
    /// <c>PluginHostOptions.MaxUnannouncedConnections</c>, which is the point: connections that
    /// have not said hello are capped separately and far lower, so silence cannot exhaust this.
    /// </remarks>
    public const int MaxInstances = 16;

    /// <summary>Creates a server stream with the plugin channel's access policy applied.</summary>
    public static NamedPipeServerStream Create() =>
        NamedPipeServerStreamAcl.Create(
            PluginProtocol.PipeName,
            PipeDirection.InOut,
            MaxInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough,
            inBufferSize: 0,
            outBufferSize: 0,
            BuildSecurity());

    private static PipeSecurity BuildSecurity()
    {
        var security = new PipeSecurity();

        // FullControl for the identity the engine runs as, because every instance after the first
        // needs CreateNewInstance on a pipe the server itself created.
        if (WindowsIdentity.GetCurrent().User is { } self)
        {
            security.AddAccessRule(new PipeAccessRule(
                self,
                PipeAccessRights.FullControl,
                AccessControlType.Allow));
        }

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        // Read, write and synchronize — but never CreateNewInstance. A plugin may talk to the
        // engine; it may not stand up a second listener on this name and harvest the handshakes
        // of every other plugin on the machine.
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize,
            AccessControlType.Allow));

        return security;
    }
}
