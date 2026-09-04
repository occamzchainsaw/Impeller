using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Impeller.Platform.Windows;

/// <summary>Which program, and which account, is at the far end of a pipe.</summary>
/// <param name="ImagePath">The client executable's full path, or null when it could not be read.</param>
/// <param name="UserSid">The client's user SID, or null when it could not be read.</param>
public sealed record ClientProcess(string? ImagePath, string? UserSid);

/// <summary>
/// Works out what is on the other end of a named pipe.
/// </summary>
/// <remarks>
/// <para>
/// This exists to bind a standing permission to a program rather than to a string. Without it, a
/// plugin approval keys off nothing but a manifest id, and any program that announces the right id
/// inherits every fan the user ever granted.
/// </para>
/// <para>
/// <strong>It is not a boundary against a same-user adversary, and nothing built on it should
/// imply that it is.</strong> Mapping a process id to a path has an inherent reuse race; anyone who
/// can write to the approved program's install directory wins; anyone who can inject into the
/// approved process wins. What it does defend against is <em>silent</em> inheritance — a different
/// program quietly picking up a grant the user gave to something else — and that is worth the
/// twenty lines it costs.
/// </para>
/// <para>
/// Every failure is answered with nulls rather than an exception. Not being able to name the
/// program at the other end is a reason to prompt the user again, not a reason to refuse to run.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static partial class PipeClientIdentity
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    /// <summary>Identifies the client of a connected server pipe.</summary>
    public static ClientProcess Identify(NamedPipeServerStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);

        return new ClientProcess(ReadImagePath(pipe), ReadUserSid(pipe));
    }

    private static string? ReadImagePath(NamedPipeServerStream pipe)
    {
        try
        {
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var processId))
            {
                return null;
            }

            // Limited information only: enough for the path, and not enough to read the process's
            // memory. The engine runs as LocalSystem and should ask for the least it can.
            using var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);

            if (process.IsInvalid)
            {
                return null;
            }

            // A stack buffer and a pointer rather than a marshalled array: the source-generated
            // marshaller refuses char[] unless runtime marshalling is disabled assembly-wide, and
            // turning that off for the whole project to fill one buffer is the wrong trade.
            Span<char> buffer = stackalloc char[1024];
            var length = (uint)buffer.Length;

            unsafe
            {
                fixed (char* start = buffer)
                {
                    if (!QueryFullProcessImageNameW(process, 0, start, ref length))
                    {
                        return null;
                    }
                }
            }

            return new string(buffer[..(int)length]);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the client's account by briefly impersonating it.
    /// </summary>
    /// <remarks>
    /// Impersonation rather than opening the client's token, because it needs no rights over the
    /// client process at all: the pipe already carries the caller's identity, and this asks Windows
    /// who that is. The impersonation lasts one property read.
    /// </remarks>
    private static string? ReadUserSid(NamedPipeServerStream pipe)
    {
        string? sid = null;

        try
        {
            pipe.RunAsClient(() => sid = WindowsIdentity.GetCurrent().User?.Value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }

        return sid;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageNameW(
        SafeProcessHandle process,
        uint flags,
        char* buffer,
        ref uint size);
}
