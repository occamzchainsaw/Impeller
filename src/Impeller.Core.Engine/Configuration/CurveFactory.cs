using Impeller.Core.Abstractions;
using Impeller.Core.Abstractions.Configuration;
using Impeller.Core.Engine.Curves;

namespace Impeller.Core.Engine.Configuration;

/// <summary>
/// Turns stored curve definitions into the objects that evaluate them, and back again.
/// </summary>
/// <remarks>
/// The two directions are kept together on purpose. They have to agree — anything the factory can
/// build it must also be able to describe, or editing a curve in the UI and saving it would
/// quietly lose whatever the describe side forgot — and a round-trip test over a single class is a
/// far better guard against that than two classes that drifted apart politely.
/// </remarks>
public static class CurveFactory
{
    /// <summary>
    /// Builds the live curve a definition describes.
    /// </summary>
    /// <exception cref="ArgumentException">The definition is of a type this build does not know.</exception>
    public static IFanCurve Create(CurveDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return definition switch
        {
            FlatCurveDefinition flat =>
                new FlatCurve(flat.Id, flat.Name, flat.Duty),

            LinearCurveDefinition linear =>
                new LinearCurve(
                    linear.Id,
                    linear.Name,
                    linear.Source,
                    linear.MinimumInput,
                    linear.MaximumInput,
                    linear.MinimumDuty,
                    linear.MaximumDuty,
                    ToSettings(linear.Hysteresis)),

            GraphCurveDefinition graph =>
                new GraphCurve(
                    graph.Id,
                    graph.Name,
                    graph.Source,
                    graph.Points.Select(point => new CurvePoint(point.Input, point.Duty)),
                    ToSettings(graph.Hysteresis)),

            MixCurveDefinition mix =>
                new MixCurve(mix.Id, mix.Name, mix.Function, mix.Sources),

            SyncCurveDefinition sync => CreateSync(sync),

            TriggerCurveDefinition trigger =>
                new TriggerCurve(
                    trigger.Id,
                    trigger.Name,
                    trigger.Source,
                    trigger.IdleInput,
                    trigger.LoadInput,
                    trigger.IdleDuty,
                    trigger.LoadDuty,
                    trigger.ResponseUp,
                    trigger.ResponseDown),

            AutoCurveDefinition auto =>
                new AutoCurve(
                    auto.Id,
                    auto.Name,
                    auto.Source,
                    auto.IdleTemperature,
                    auto.LoadTemperature,
                    auto.MinimumDuty,
                    auto.MaximumDuty,
                    auto.Step,
                    auto.Deadband,
                    auto.ResponseTime),

            _ => throw new ArgumentException(
                $"'{definition.GetType().Name}' is not a curve type this build can create.",
                nameof(definition)),
        };
    }

    /// <summary>
    /// Describes a live curve as the definition that would rebuild it.
    /// </summary>
    /// <exception cref="ArgumentException">The curve is of a type this build cannot describe.</exception>
    public static CurveDefinition Describe(IFanCurve curve)
    {
        ArgumentNullException.ThrowIfNull(curve);

        return curve switch
        {
            FlatCurve flat => new FlatCurveDefinition
            {
                Id = flat.Id,
                Name = flat.Name,
                Duty = flat.Duty,
            },

            LinearCurve linear => new LinearCurveDefinition
            {
                Id = linear.Id,
                Name = linear.Name,
                Source = linear.Source,
                MinimumInput = linear.MinimumInput,
                MaximumInput = linear.MaximumInput,
                MinimumDuty = linear.MinimumDuty,
                MaximumDuty = linear.MaximumDuty,
                Hysteresis = ToDefinition(linear.Hysteresis),
            },

            GraphCurve graph => new GraphCurveDefinition
            {
                Id = graph.Id,
                Name = graph.Name,
                Source = graph.Source,
                Points = [.. graph.Points.Select(point => new CurvePointDefinition(point.Input, point.Duty))],
                Hysteresis = ToDefinition(graph.Hysteresis),
            },

            MixCurve mix => new MixCurveDefinition
            {
                Id = mix.Id,
                Name = mix.Name,
                Function = mix.Function,
                Sources = [.. mix.Sources],
            },

            SyncCurve sync => new SyncCurveDefinition
            {
                Id = sync.Id,
                Name = sync.Name,
                SourceKind = sync.SourceKind,
                SourceCurve = sync.SourceCurve,
                SourceControl = sync.SourceControl,
                Offset = sync.Offset,
                Proportional = sync.Proportional,
            },

            TriggerCurve trigger => new TriggerCurveDefinition
            {
                Id = trigger.Id,
                Name = trigger.Name,
                Source = trigger.Source,
                IdleInput = trigger.IdleInput,
                LoadInput = trigger.LoadInput,
                IdleDuty = trigger.IdleDuty,
                LoadDuty = trigger.LoadDuty,
                ResponseUp = trigger.ResponseUp,
                ResponseDown = trigger.ResponseDown,
            },

            AutoCurve auto => new AutoCurveDefinition
            {
                Id = auto.Id,
                Name = auto.Name,
                Source = auto.Source,
                IdleTemperature = auto.IdleTemperature,
                LoadTemperature = auto.LoadTemperature,
                MinimumDuty = auto.MinimumDuty,
                MaximumDuty = auto.MaximumDuty,
                Step = auto.Step,
                Deadband = auto.Deadband,
                ResponseTime = auto.ResponseTime,
            },

            _ => throw new ArgumentException(
                $"'{curve.GetType().Name}' is not a curve type this build can describe.",
                nameof(curve)),
        };
    }

    /// <summary>Builds a binding from its definition.</summary>
    public static ControlBinding CreateBinding(ControlBindingDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return new ControlBinding(definition.ControlId)
        {
            CurveId = definition.CurveId,
            Enabled = definition.Enabled,
            MinimumDuty = definition.MinimumDuty,
            MaximumDuty = definition.MaximumDuty,
            FailsafeDuty = definition.FailsafeDuty,
            MaximumStepUpPerSecond = definition.MaximumStepUpPerSecond,
            MaximumStepDownPerSecond = definition.MaximumStepDownPerSecond,
            StartDuty = definition.StartDuty,
            StopDuty = definition.StopDuty,
            PairedFanSensorId = definition.PairedFanSensorId,
            ManualDuty = definition.ManualDuty,
        };
    }

    /// <summary>
    /// Describes a binding, preserving the calibration table it was built with.
    /// </summary>
    /// <remarks>
    /// Calibration is measured, not configured, and the engine does not currently act on the
    /// table beyond start and stop — so it lives on the definition rather than the live binding
    /// and is carried across here rather than reconstructed.
    /// </remarks>
    public static ControlBindingDefinition Describe(
        ControlBinding binding,
        EquatableArray<CalibrationPointDefinition> calibration = default)
    {
        ArgumentNullException.ThrowIfNull(binding);

        return new ControlBindingDefinition
        {
            ControlId = binding.ControlId,
            CurveId = binding.CurveId,
            Enabled = binding.Enabled,
            MinimumDuty = binding.MinimumDuty,
            MaximumDuty = binding.MaximumDuty,
            FailsafeDuty = binding.FailsafeDuty,
            MaximumStepUpPerSecond = binding.MaximumStepUpPerSecond,
            MaximumStepDownPerSecond = binding.MaximumStepDownPerSecond,
            StartDuty = binding.StartDuty,
            StopDuty = binding.StopDuty,
            PairedFanSensorId = binding.PairedFanSensorId,
            ManualDuty = binding.ManualDuty,
            Calibration = calibration,
        };
    }

    private static SyncCurve CreateSync(SyncCurveDefinition sync) =>
        sync.SourceKind == SyncSourceKind.Curve
            ? new SyncCurve(sync.Id, sync.Name, sync.SourceCurve, sync.Offset, sync.Proportional)
            : new SyncCurve(sync.Id, sync.Name, sync.SourceControl, sync.Offset, sync.Proportional);

    private static HysteresisSettings ToSettings(HysteresisDefinition definition) =>
        new(definition.DeadbandUp, definition.DeadbandDown, definition.ResponseUp, definition.ResponseDown);

    private static HysteresisDefinition ToDefinition(HysteresisSettings settings) =>
        new(settings.DeadbandUp, settings.DeadbandDown, settings.ResponseUp, settings.ResponseDown);
}
