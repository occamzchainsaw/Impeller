using Impeller.Core.Abstractions;
using Impeller.Core.Engine.Tuning;

namespace Impeller.Core.Engine.Tests.Tuning;

/// <summary>
/// Covers working out which tacho belongs to which header by moving one fan at a time.
/// </summary>
/// <remarks>
/// The interesting failures are not "it found nothing" but "it found the wrong one": a header
/// paired with its neighbour's tacho produces a configuration where the start logic watches the
/// wrong fan, and nothing about the result looks wrong until a fan will not start.
/// </remarks>
public class FanPairingRunTests
{
    /// <summary>
    /// A machine with a known wiring, so a run can be checked against the truth.
    /// </summary>
    /// <remarks>
    /// The cross-talk is the point. Fans share airflow, so slowing one really does slow its
    /// neighbours a little, and a procedure that takes the first sensor to move rather than the one
    /// that moved most will eventually take the wrong one.
    /// </remarks>
    private sealed class Machine(float crossTalk = 0f)
    {
        private readonly Dictionary<SensorId, SensorId> _wiring = [];
        private readonly Dictionary<SensorId, float> _speeds = [];
        private readonly Dictionary<SensorId, Duty> _commands = [];

        public List<SensorId> Controls { get; } = [];

        public List<SensorId> Tachometers { get; } = [];

        /// <summary>Wires one control to one tacho and returns the pair.</summary>
        public (SensorId Control, SensorId Tachometer) Wire()
        {
            var control = SensorId.New();
            var tachometer = SensorId.New();

            Controls.Add(control);
            Tachometers.Add(tachometer);
            _wiring[control] = tachometer;
            _commands[control] = Duty.Off;
            _speeds[tachometer] = 0f;

            return (control, tachometer);
        }

        /// <summary>A tacho on a fan nothing in this run drives.</summary>
        public SensorId AddUnwiredTachometer()
        {
            var tachometer = SensorId.New();
            Tachometers.Add(tachometer);
            _speeds[tachometer] = 900f;
            return tachometer;
        }

        /// <summary>A header with no fan plugged into it, which every machine has a couple of.</summary>
        public SensorId AddEmptyHeader()
        {
            var control = SensorId.New();
            Controls.Add(control);
            _commands[control] = Duty.Off;
            return control;
        }

        public void Command(SensorId control, Duty duty) => _commands[control] = duty;

        /// <summary>Advances every fan one sample toward the speed its duty implies.</summary>
        public Dictionary<SensorId, float?> Sample()
        {
            var average = _commands.Count == 0
                ? 0f
                : _commands.Values.Average(duty => duty.Percent);

            foreach (var (control, duty) in _commands)
            {
                if (!_wiring.TryGetValue(control, out var tachometer))
                {
                    continue;
                }

                // Its own duty, pulled slightly by what the rest of the case is doing.
                var effective = (duty.Percent * (1f - crossTalk)) + (average * crossTalk);
                var target = 200f + (16f * effective);

                _speeds[tachometer] += (target - _speeds[tachometer]) * 0.6f;
            }

            return _speeds.ToDictionary(entry => entry.Key, entry => (float?)entry.Value);
        }
    }

    /// <summary>Drives a run to completion against a machine.</summary>
    private static FanPairingRun Pair(Machine machine, FanPairingSettings? settings = null)
    {
        var run = new FanPairingRun(machine.Controls, machine.Tachometers, settings);

        for (var sample = 0; sample < 5000 && !run.IsComplete; sample++)
        {
            foreach (var control in machine.Controls)
            {
                machine.Command(control, run.CommandFor(control));
            }

            run.Advance(machine.Sample());
        }

        Assert.True(run.IsComplete, $"The run was still in {run.Phase} after 5000 samples.");
        return run;
    }

    [Fact]
    public void Each_control_is_matched_with_the_fan_it_actually_drives()
    {
        var machine = new Machine();
        var wiring = Enumerable.Range(0, 4).Select(_ => machine.Wire()).ToList();

        var run = Pair(machine);

        Assert.Equal(4, run.Pairs.Count);

        foreach (var (control, tachometer) in wiring)
        {
            Assert.Equal(tachometer, run.Pairs[control]);
        }
    }

    [Fact]
    public void Cross_talk_between_neighbouring_fans_does_not_produce_a_wrong_pairing()
    {
        // Every fan in the case moves a little when any one of them does. The one that moved most
        // is the one that is wired to the header; the first one over the line is whichever the
        // dictionary happened to enumerate first.
        var machine = new Machine(crossTalk: 0.25f);
        var wiring = Enumerable.Range(0, 4).Select(_ => machine.Wire()).ToList();

        var run = Pair(machine);

        foreach (var (control, tachometer) in wiring)
        {
            Assert.Equal(tachometer, run.Pairs[control]);
        }
    }

    [Fact]
    public void A_tacho_already_claimed_is_not_offered_to_a_second_control()
    {
        var machine = new Machine(crossTalk: 0.25f);
        for (var i = 0; i < 4; i++)
        {
            machine.Wire();
        }

        var run = Pair(machine);

        Assert.Equal(run.Pairs.Count, run.Pairs.Values.Distinct().Count());
    }

    [Fact]
    public void A_header_with_nothing_on_it_is_reported_unpaired_rather_than_guessed_at()
    {
        // An empty header is ordinary. Handing it the nearest tacho would silently attach the start
        // logic of one fan to the speed of another.
        var machine = new Machine();
        var (wired, tachometer) = machine.Wire();
        var empty = machine.AddEmptyHeader();

        var run = new FanPairingRun(machine.Controls, machine.Tachometers, new FanPairingSettings
        {
            SettleTimeout = 20,
            TestTimeout = 10,
        });

        for (var sample = 0; sample < 500 && !run.IsComplete; sample++)
        {
            foreach (var control in machine.Controls)
            {
                machine.Command(control, run.CommandFor(control));
            }

            run.Advance(machine.Sample());
        }

        Assert.True(run.IsComplete);
        Assert.Equal(tachometer, run.Pairs[wired]);
        Assert.False(run.Pairs.ContainsKey(empty));
    }

    [Fact]
    public void A_fan_nothing_here_drives_is_left_alone()
    {
        var machine = new Machine();
        var (control, tachometer) = machine.Wire();
        var stranger = machine.AddUnwiredTachometer();

        var run = Pair(machine);

        Assert.Equal(tachometer, run.Pairs[control]);
        Assert.DoesNotContain(stranger, run.Pairs.Values);
    }

    [Fact]
    public void Everything_not_under_test_is_held_at_the_baseline()
    {
        // A control left on its own curve would change speed for its own reasons partway through,
        // and there is no way to tell that apart from the drop the run is looking for.
        var machine = new Machine();
        for (var i = 0; i < 3; i++)
        {
            machine.Wire();
        }

        var run = new FanPairingRun(machine.Controls, machine.Tachometers);

        for (var sample = 0; sample < 200 && run.Phase != PairingPhase.Testing; sample++)
        {
            foreach (var control in machine.Controls)
            {
                machine.Command(control, run.CommandFor(control));
            }

            run.Advance(machine.Sample());
        }

        Assert.Equal(PairingPhase.Testing, run.Phase);

        foreach (var control in machine.Controls)
        {
            var expected = control == run.CurrentControl ? 35f : 75f;
            Assert.Equal(expected, run.CommandFor(control).Percent);
        }
    }

    [Fact]
    public void A_machine_with_no_tachometers_finishes_immediately()
    {
        var run = new FanPairingRun([SensorId.New()], []);

        Assert.True(run.IsComplete);
        Assert.Empty(run.Pairs);
    }
}
