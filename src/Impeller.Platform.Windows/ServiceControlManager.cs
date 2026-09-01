using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Impeller.Platform.Windows;

/// <summary>
/// Installs, removes and queries the Impeller engine service.
/// </summary>
/// <remarks>
/// <para>
/// Talks to the Service Control Manager directly rather than shelling out to <c>sc.exe</c> and
/// reading its output. FanControl does the latter, and it is the weakest part of its installer:
/// <c>sc.exe</c> prints localised text, so "is this service installed?" becomes a string match
/// that silently answers wrong on a non-English Windows. The API returns typed error codes that
/// mean the same thing in every locale.
/// </para>
/// <para>
/// The service runs as <c>LocalSystem</c>. That is what gives it the privileges
/// LibreHardwareMonitor's kernel driver needs, and it is the same account FanControl's own service
/// uses. Session 0 costs nothing here because the engine has no UI — the shell is a separate,
/// unelevated process that talks to it over IPC.
/// </para>
/// <para>
/// Failure actions are configured on install: restart after 1 minute, then 5, then 10, with the
/// count resetting after an hour. A fan controller that dies silently leaves every fan at whatever
/// duty was last written, with nothing maintaining it. Letting the SCM restart the process is the
/// cheapest real answer to that, and it is why the engine applies its failsafe on the way out.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static partial class ServiceControlManager
{
    /// <summary>The service's key name, as it appears in the SCM database.</summary>
    public const string ServiceName = "ImpellerEngine";

    /// <summary>The name shown in services.msc.</summary>
    public const string DisplayName = "Impeller Engine";

    private const string Description =
        "Runs Impeller's fan control loop: reads hardware sensors and drives fan and pump "
        + "controls. Stopping this service returns fans to their failsafe duty.";

    /// <summary>Installs the service and configures its restart behaviour.</summary>
    /// <param name="executablePath">Full path to the engine executable.</param>
    /// <exception cref="Win32Exception">The SCM refused the operation.</exception>
    public static void Install(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException("Engine executable not found.", executablePath);
        }

        using var manager = OpenManager();

        // Quoted, because an unquoted binary path containing spaces is the classic unquoted
        // service path privilege-escalation hole.
        var binaryPath = $"\"{executablePath}\"";

        var service = CreateServiceW(
            manager,
            ServiceName,
            DisplayName,
            SERVICE_ALL_ACCESS,
            SERVICE_WIN32_OWN_PROCESS,
            SERVICE_AUTO_START,
            SERVICE_ERROR_NORMAL,
            binaryPath,
            null,
            IntPtr.Zero,
            null,
            null,   // null service account means LocalSystem.
            null);

        if (service.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the service.");
        }

        using (service)
        {
            SetDescription(service);
            SetFailureActions(service);
        }
    }

    /// <summary>Stops the service if running, then removes it. Safe to call when not installed.</summary>
    public static bool Uninstall()
    {
        using var manager = OpenManager();
        using var service = OpenServiceW(manager, ServiceName, SERVICE_ALL_ACCESS);

        if (service.IsInvalid)
        {
            return false;
        }

        TryStop(service);

        if (!DeleteService(service))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not delete the service.");
        }

        return true;
    }

    /// <summary>Whether the service is registered with the SCM.</summary>
    public static bool IsInstalled()
    {
        using var manager = OpenManager(SC_MANAGER_CONNECT);
        using var service = OpenServiceW(manager, ServiceName, SERVICE_QUERY_STATUS);
        return !service.IsInvalid;
    }

    /// <summary>The service's current state, or <see langword="null"/> when it is not installed.</summary>
    public static ServiceState? Query()
    {
        using var manager = OpenManager(SC_MANAGER_CONNECT);
        using var service = OpenServiceW(manager, ServiceName, SERVICE_QUERY_STATUS);

        if (service.IsInvalid)
        {
            return null;
        }

        return QueryServiceStatus(service, out var status)
            ? (ServiceState)status.CurrentState
            : null;
    }

    /// <summary>Starts the service. Returns false if it was already running.</summary>
    public static bool Start()
    {
        using var manager = OpenManager();
        using var service = OpenServiceW(manager, ServiceName, SERVICE_ALL_ACCESS);

        if (service.IsInvalid)
        {
            throw new InvalidOperationException("The service is not installed.");
        }

        if (StartServiceW(service, 0, IntPtr.Zero))
        {
            return true;
        }

        var error = Marshal.GetLastWin32Error();

        return error == ERROR_SERVICE_ALREADY_RUNNING
            ? false
            : throw new Win32Exception(error, "Could not start the service.");
    }

    /// <summary>Stops the service. Returns false if it was not running.</summary>
    public static bool Stop()
    {
        using var manager = OpenManager();
        using var service = OpenServiceW(manager, ServiceName, SERVICE_ALL_ACCESS);

        return !service.IsInvalid && TryStop(service);
    }

    private static bool TryStop(SafeServiceHandle service)
    {
        if (!ControlService(service, SERVICE_CONTROL_STOP, out _))
        {
            var error = Marshal.GetLastWin32Error();

            if (error is ERROR_SERVICE_NOT_ACTIVE)
            {
                return false;
            }

            throw new Win32Exception(error, "Could not stop the service.");
        }

        // The engine writes failsafe duties during shutdown, so waiting matters: deleting the
        // service out from under a process that is still handing fans back to firmware would
        // leave them wherever the last curve put them.
        var deadline = DateTime.UtcNow.AddSeconds(30);

        while (DateTime.UtcNow < deadline)
        {
            if (!QueryServiceStatus(service, out var status) ||
                status.CurrentState == SERVICE_STOPPED)
            {
                return true;
            }

            Thread.Sleep(250);
        }

        return true;
    }

    private static void SetDescription(SafeServiceHandle service)
    {
        var descriptionPointer = Marshal.StringToHGlobalUni(Description);

        try
        {
            var info = new SERVICE_DESCRIPTION { Description = descriptionPointer };
            var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<SERVICE_DESCRIPTION>());

            try
            {
                Marshal.StructureToPtr(info, buffer, fDeleteOld: false);
                ChangeServiceConfig2W(service, SERVICE_CONFIG_DESCRIPTION, buffer);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(descriptionPointer);
        }
    }

    private static void SetFailureActions(SafeServiceHandle service)
    {
        SC_ACTION[] actions =
        [
            new() { Type = SC_ACTION_RESTART, Delay = 60_000 },
            new() { Type = SC_ACTION_RESTART, Delay = 300_000 },
            new() { Type = SC_ACTION_RESTART, Delay = 600_000 },
        ];

        var actionSize = Marshal.SizeOf<SC_ACTION>();
        var actionsBuffer = Marshal.AllocHGlobal(actionSize * actions.Length);

        try
        {
            for (var i = 0; i < actions.Length; i++)
            {
                Marshal.StructureToPtr(actions[i], actionsBuffer + (i * actionSize), fDeleteOld: false);
            }

            var failureActions = new SERVICE_FAILURE_ACTIONS
            {
                ResetPeriod = 3600,
                RebootMessage = IntPtr.Zero,
                Command = IntPtr.Zero,
                ActionCount = actions.Length,
                Actions = actionsBuffer,
            };

            var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<SERVICE_FAILURE_ACTIONS>());

            try
            {
                Marshal.StructureToPtr(failureActions, buffer, fDeleteOld: false);

                if (!ChangeServiceConfig2W(service, SERVICE_CONFIG_FAILURE_ACTIONS, buffer))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "The service was created but its restart policy could not be set.");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(actionsBuffer);
        }
    }

    private static SafeServiceHandle OpenManager(uint access = SC_MANAGER_ALL_ACCESS)
    {
        var manager = OpenSCManagerW(null, null, access);

        if (manager.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();

            throw error == ERROR_ACCESS_DENIED
                ? new UnauthorizedAccessException(
                    "Administrator rights are required to manage the Impeller engine service.")
                : new Win32Exception(error, "Could not open the service control manager.");
        }

        return manager;
    }

    private const uint SC_MANAGER_ALL_ACCESS = 0xF003F;
    private const uint SC_MANAGER_CONNECT = 0x0001;
    private const uint SERVICE_ALL_ACCESS = 0xF01FF;
    private const uint SERVICE_QUERY_STATUS = 0x0004;
    private const uint SERVICE_WIN32_OWN_PROCESS = 0x00000010;
    private const uint SERVICE_AUTO_START = 0x00000002;
    private const uint SERVICE_ERROR_NORMAL = 0x00000001;
    private const uint SERVICE_CONTROL_STOP = 0x00000001;
    private const uint SERVICE_CONFIG_DESCRIPTION = 1;
    private const uint SERVICE_CONFIG_FAILURE_ACTIONS = 2;
    private const uint SC_ACTION_RESTART = 1;
    private const uint SERVICE_STOPPED = 0x00000001;
    private const int ERROR_ACCESS_DENIED = 5;
    private const int ERROR_SERVICE_ALREADY_RUNNING = 1056;
    private const int ERROR_SERVICE_NOT_ACTIVE = 1062;

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeServiceHandle OpenSCManagerW(string? machineName, string? databaseName, uint access);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeServiceHandle OpenServiceW(SafeServiceHandle manager, string serviceName, uint access);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
#pragma warning disable SYSLIB1054 // Too many parameters for a readable LibraryImport signature.
    private static partial SafeServiceHandle CreateServiceW(
        SafeServiceHandle manager,
        string serviceName,
        string displayName,
        uint desiredAccess,
        uint serviceType,
        uint startType,
        uint errorControl,
        string binaryPath,
        string? loadOrderGroup,
        IntPtr tagId,
        string? dependencies,
        string? serviceStartName,
        string? password);
#pragma warning restore SYSLIB1054

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteService(SafeServiceHandle service);

    [LibraryImport("advapi32.dll", SetLastError = true, EntryPoint = "StartServiceW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool StartServiceW(SafeServiceHandle service, uint argCount, IntPtr argVectors);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ControlService(SafeServiceHandle service, uint control, out SERVICE_STATUS status);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceStatus(SafeServiceHandle service, out SERVICE_STATUS status);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChangeServiceConfig2W(SafeServiceHandle service, uint infoLevel, IntPtr info);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseServiceHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_DESCRIPTION
    {
        public IntPtr Description;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SC_ACTION
    {
        public uint Type;
        public uint Delay;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_FAILURE_ACTIONS
    {
        public uint ResetPeriod;
        public IntPtr RebootMessage;
        public IntPtr Command;
        public int ActionCount;
        public IntPtr Actions;
    }
}

/// <summary>The SCM's view of a service's lifecycle.</summary>
public enum ServiceState
{
    /// <summary>Not running.</summary>
    Stopped = 1,

    /// <summary>Start requested, not yet running.</summary>
    StartPending = 2,

    /// <summary>Stop requested, not yet stopped.</summary>
    StopPending = 3,

    /// <summary>Running.</summary>
    Running = 4,

    /// <summary>Continue requested after a pause.</summary>
    ContinuePending = 5,

    /// <summary>Pause requested.</summary>
    PausePending = 6,

    /// <summary>Paused.</summary>
    Paused = 7,
}

/// <summary>A service or SCM handle that closes itself.</summary>
[SupportedOSPlatform("windows")]
internal sealed class SafeServiceHandle() : SafeHandle(IntPtr.Zero, ownsHandle: true)
{
    /// <inheritdoc />
    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <inheritdoc />
    protected override bool ReleaseHandle() => ServiceControlManager.CloseServiceHandle(handle);
}
