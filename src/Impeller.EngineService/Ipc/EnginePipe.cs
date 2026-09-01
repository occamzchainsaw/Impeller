using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Impeller.Ipc.Contracts;

namespace Impeller.EngineService.Ipc;

/// <summary>
/// Creates the listening end of the engine's named pipe, with the access it is meant to have.
/// </summary>
/// <remarks>
/// <para>
/// The engine runs as LocalSystem and the shell runs as whoever is signed in, so the pipe has to
/// be reachable across that gap. Left to the default, a pipe created by a service is not.
/// </para>
/// <para>
/// Any signed-in user gets read and write. That is a real decision rather than a default: writing
/// is how a fan gets set, so a read-only grant would mean elevating the shell — a UAC prompt to
/// move a slider, which people would rightly work around. It is also the same trust level the app
/// being replaced has by running wholly as the user. The exposure is that any local process can
/// command a fan; the mitigation is that any local process able to ship its own driver could do
/// that anyway.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class EnginePipe
{
    /// <summary>How many connections the engine will serve at once.</summary>
    /// <remarks>
    /// Several, deliberately: a main window and a tray icon are two clients of the same engine, and
    /// a shell that crashed without closing cleanly should not lock the next one out.
    /// </remarks>
    public const int MaxInstances = 8;

    /// <summary>Creates a server stream with the engine's access policy applied.</summary>
    public static NamedPipeServerStream Create() =>
        NamedPipeServerStreamAcl.Create(
            ImpellerPipe.Name,
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

        // The identity the engine runs as, which needs FullControl for a reason that is easy to
        // miss: every instance after the first requires CreateNewInstance on the pipe that already
        // exists. Without this the first client connects, the accept loop tries to open the next
        // instance, and is refused by a descriptor the server wrote itself.
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

        // ReadWrite plus Synchronize, and the Synchronize matters more than it looks: without it a
        // client is refused, and .NET reports that refusal as a connection timeout rather than an
        // access error — so the symptom is "the engine is not running" on a machine where it
        // demonstrably is. Not CreateNewInstance, though: a client may talk to the engine, not
        // stand up a second listener on the same name and pretend to be it.
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize,
            AccessControlType.Allow));

        return security;
    }
}
