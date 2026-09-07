using System.Runtime.InteropServices;

namespace Impeller.Hardware.Adlx;

/// <summary>Every ADLX call this provider makes, and nothing else.</summary>
/// <remarks>
/// <para>
/// ADLX exposes a C interface of COM-style objects: each pointer's first field is a vtable of
/// stdcall function pointers, called by index. There is no type library and no managed binding,
/// so the indices below come from AMD's public headers and are the whole contract. They are
/// declared as named constants rather than written inline because an off-by-one here calls a
/// different function with the same signature, which does not crash — it silently does the wrong
/// thing to somebody's graphics card.
/// </para>
/// <para>
/// Only the fan-tuning path is bound. ADLX can do a great deal more, and every one of those
/// vtable slots would be another number that has to be right.
/// </para>
/// </remarks>
internal static unsafe class Adlx
{
    /// <summary>The library is part of the AMD display driver, so its absence is ordinary.</summary>
    private const string Library = "amdadlx64.dll";

    [DllImport(Library)]
    internal static extern AdlxResult ADLXQueryFullVersion(ulong* version);

    [DllImport(Library)]
    internal static extern AdlxResult ADLXInitialize(ulong version, void** system);

    [DllImport(Library)]
    internal static extern AdlxResult ADLXTerminate();

    /// <summary>Vtable slots on <c>IADLXSystem</c>, which has no Acquire/Release of its own.</summary>
    internal static class System
    {
        internal const int GetGPUs = 1;
        internal const int GetGPUTuningServices = 8;
    }

    /// <summary>Vtable slots shared by every ADLX list type.</summary>
    internal static class List
    {
        internal const int Size = 3;
        internal const int Begin = 5;

        /// <summary>The typed accessor, which every list declares after the untyped ones.</summary>
        internal const int AtTyped = 11;
    }

    /// <summary>Vtable slots on <c>IADLXGPUTuningServices</c>.</summary>
    internal static class Tuning
    {
        internal const int Release = 1;
        internal const int IsSupportedManualFanTuning = 10;
        internal const int GetManualFanTuning = 16;
    }

    /// <summary>Vtable slots on <c>IADLXManualFanTuning</c>.</summary>
    internal static class Fan
    {
        internal const int Release = 1;
        internal const int GetFanTuningRanges = 3;
        internal const int GetFanTuningStates = 4;
        internal const int IsValidFanTuningStates = 6;
        internal const int SetFanTuningStates = 7;
        internal const int IsSupportedZeroRPM = 8;
        internal const int GetZeroRPMState = 9;
        internal const int SetZeroRPMState = 10;
        internal const int IsSupportedTargetFanSpeed = 11;
        internal const int GetTargetFanSpeed = 13;
        internal const int SetTargetFanSpeed = 14;
    }

    /// <summary>Vtable slots on <c>IADLXManualFanTuningState</c>, one point of a fan curve.</summary>
    internal static class State
    {
        internal const int GetFanSpeed = 3;
        internal const int SetFanSpeed = 4;
        internal const int GetTemperature = 5;
        internal const int SetTemperature = 6;
    }

    /// <summary>The vtable behind an ADLX object pointer.</summary>
    internal static void** Vtbl(void* self) => *(void***)self;

    internal static AdlxResult Out(void* self, int slot, void** result) =>
        ((delegate* unmanaged[Stdcall]<void*, void**, AdlxResult>)Vtbl(self)[slot])(self, result);

    internal static AdlxResult Out(void* self, int slot, int* result) =>
        ((delegate* unmanaged[Stdcall]<void*, int*, AdlxResult>)Vtbl(self)[slot])(self, result);

    internal static AdlxResult In(void* self, int slot, int value) =>
        ((delegate* unmanaged[Stdcall]<void*, int, AdlxResult>)Vtbl(self)[slot])(self, value);

    internal static AdlxResult ForGpu(void* self, int slot, void* gpu, int* result) =>
        ((delegate* unmanaged[Stdcall]<void*, void*, int*, AdlxResult>)Vtbl(self)[slot])(self, gpu, result);

    internal static AdlxResult ForGpu(void* self, int slot, void* gpu, void** result) =>
        ((delegate* unmanaged[Stdcall]<void*, void*, void**, AdlxResult>)Vtbl(self)[slot])(self, gpu, result);

    internal static uint Count(void* list, int slot) =>
        ((delegate* unmanaged[Stdcall]<void*, uint>)Vtbl(list)[slot])(list);

    internal static AdlxResult At(void* list, uint index, void** item) =>
        ((delegate* unmanaged[Stdcall]<void*, uint, void**, AdlxResult>)Vtbl(list)[List.AtTyped])(list, index, item);

    internal static void Release(void* self, int slot)
    {
        if (self is not null)
        {
            ((delegate* unmanaged[Stdcall]<void*, int>)Vtbl(self)[slot])(self);
        }
    }
}

/// <summary>ADLX's own result codes, as its headers declare them.</summary>
internal enum AdlxResult
{
    Ok = 0,
    AlreadyEnabled,
    AlreadyInitialized,
    Fail,
    InvalidArgs,
    BadVersion,
    UnknownInterface,
    Terminated,
    AdlInitError,
    NotFound,
    InvalidObject,
    OrphanObjects,
    NotSupported,
    PendingOperation,
    GpuInactive,
    GpuInUse,
    TimeoutOperation,
    NotActive,
    ResetNeeded,
}

/// <summary>A minimum, maximum and step, as ADLX reports tuning ranges.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct AdlxIntRange
{
    internal int Min;
    internal int Max;
    internal int Step;
}
