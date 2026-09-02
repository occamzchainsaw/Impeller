using Impeller.Core.Abstractions;
using Impeller.Core.Persistence.Legacy;

namespace Impeller.Core.Engine.Tests.Legacy;

/// <summary>
/// Covers the translation from a stored hardware path back to a fingerprint.
/// </summary>
/// <remarks>
/// The zero-index cases are the ones that matter. A single Super I/O chip is the common case on a
/// desktop motherboard, and getting its path wrong means every motherboard fan header quietly fails
/// to resolve while everything else about the import looks fine.
/// </remarks>
public class LegacyIdentifierTests
{
    [Fact]
    public void A_plain_path_splits_into_hardware_channel_and_kind()
    {
        Assert.True(LegacyIdentifier.TryParse("/amdcpu/0/temperature/2", out var fingerprint));

        Assert.Equal("lhm", fingerprint.ProviderId);
        Assert.Equal("/amdcpu/0", fingerprint.HardwareKey);
        Assert.Equal(2, fingerprint.Channel);
        Assert.Equal(SensorKind.Temperature, fingerprint.Kind);
    }

    [Fact]
    public void A_super_io_path_gets_back_the_zero_index_that_was_stripped_from_it()
    {
        // What the file contains, and what the hardware actually calls itself.
        Assert.Equal("/lpc/nct6687d/0/control/0", LegacyIdentifier.RestoreStrippedLpcIndex("/lpc/nct6687d/control/0"));

        var candidates = LegacyIdentifier.Candidates("/lpc/nct6687d/control/0");

        Assert.Equal("/lpc/nct6687d/0", candidates[0].HardwareKey);
        Assert.Equal(SensorKind.Control, candidates[0].Kind);
        Assert.Equal(0, candidates[0].Channel);
    }

    [Fact]
    public void The_literal_spelling_is_offered_as_well_as_the_repaired_one()
    {
        var candidates = LegacyIdentifier.Candidates("/lpc/nct6687d/control/0");

        // Repaired first, because that is the one the backend will have registered — but a board
        // that somehow stored the literal form still resolves rather than failing silently.
        Assert.Equal(2, candidates.Count);
        Assert.Equal("/lpc/nct6687d/0", candidates[0].HardwareKey);
        Assert.Equal("/lpc/nct6687d", candidates[1].HardwareKey);
    }

    [Fact]
    public void A_second_chip_keeps_the_index_it_already_has()
    {
        // Only a zero is ever stripped, so a non-zero index means nothing was removed and putting
        // one back would point at hardware that does not exist.
        Assert.Null(LegacyIdentifier.RestoreStrippedLpcIndex("/lpc/nct6687d/1/control/0"));

        var candidates = LegacyIdentifier.Candidates("/lpc/nct6687d/1/control/0");

        Assert.Single(candidates);
        Assert.Equal("/lpc/nct6687d/1", candidates[0].HardwareKey);
    }

    [Fact]
    public void A_fan_path_is_a_speed_reading_not_a_control()
    {
        Assert.True(LegacyIdentifier.TryParse("/lpc/nct6687d/0/fan/3", out var fingerprint));

        Assert.Equal(SensorKind.FanSpeed, fingerprint.Kind);
        Assert.Equal(3, fingerprint.Channel);
    }

    [Theory]
    [InlineData("ADLX/AMD Radeon RX 7800 XT/768/temp/GPU")]
    [InlineData("NvAPI/Some Card/0/temp/GPU")]
    [InlineData("Mix/CPU GPU Mix")]
    [InlineData("")]
    [InlineData("/lpc")]
    [InlineData("/amdcpu/0/temperature/not-a-number")]
    public void Anything_that_is_not_a_hardware_path_yields_no_candidates(string identifier)
    {
        Assert.Empty(LegacyIdentifier.Candidates(identifier));
    }

    [Theory]
    [InlineData("ADLX/AMD Radeon RX 7800 XT/768/temp/GPU", "AMD (ADLX)")]
    [InlineData("NvAPI/Some Card/0/temp/GPU", "NVIDIA (NvAPI)")]
    public void A_vendor_identifier_names_the_backend_it_is_waiting_on(string identifier, string expected)
    {
        // So the report can say what would fix it, rather than only that it failed.
        Assert.Equal(expected, LegacyIdentifier.PendingBackend(identifier));
    }

    [Fact]
    public void A_hardware_path_is_not_waiting_on_any_backend()
    {
        Assert.Null(LegacyIdentifier.PendingBackend("/lpc/nct6687d/control/0"));
    }
}
