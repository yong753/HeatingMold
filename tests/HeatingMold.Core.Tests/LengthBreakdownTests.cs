using HeatingMold.Core.Circuits;
using HeatingMold.Core.Electrical;
using HeatingMold.Core.Primitives;

namespace HeatingMold.Core.Tests;

/// <summary>도체 사양과 길이 원장이 재료 역할별로 합산되는지 검증합니다.</summary>
public sealed class LengthBreakdownTests
{
    /// <summary>별도 연결선은 제외하고 본선·연장·발열 여유만 저항 길이에 포함합니다.</summary>
    [Fact]
    public void ResistanceLength_ExcludesSeparateWireAndItsAllowance()
    {
        LengthLedgerEntry[] entries =
        [
            new(HeatingPathSegmentId.New(), null, LengthContributionKind.HeatingBody, GeometryDomain.OnMold,
                new LengthMm(9_000), new LengthMm(0)),
            new(null, ConnectionPathSegmentId.New(), LengthContributionKind.HeatingWireExtension, GeometryDomain.OnMold,
                new LengthMm(1_000), new LengthMm(200)),
            new(null, ConnectionPathSegmentId.New(), LengthContributionKind.SeparateConnectionWire, GeometryDomain.External,
                new LengthMm(500), new LengthMm(300))
        ];

        var breakdown = new LengthBreakdown(entries);

        Assert.Equal(9_000, breakdown.HeatingBodyMm.Value);
        Assert.Equal(1_000, breakdown.HeatingExtensionMm.Value);
        Assert.Equal(500, breakdown.SeparateConnectionWireMm.Value);
        Assert.Equal(200, breakdown.HeatingExtraAllowanceMm.Value);
        Assert.Equal(300, breakdown.SeparateConnectionWireExtraAllowanceMm.Value);
        Assert.Equal(10_200, breakdown.ResistanceLengthMm.Value);
    }

    /// <summary>같은 출처 구간을 여러 번 계상하지 못하게 합니다.</summary>
    [Fact]
    public void LengthBreakdown_RejectsDuplicateSourceSegments()
    {
        HeatingPathSegmentId source = HeatingPathSegmentId.New();
        LengthLedgerEntry[] duplicateEntries =
        [
            new(source, null, LengthContributionKind.HeatingBody, GeometryDomain.OnMold, new LengthMm(100), default),
            new(source, null, LengthContributionKind.HeatingBody, GeometryDomain.OnMold, new LengthMm(100), default)
        ];

        Assert.Throws<ArgumentException>(() => new LengthBreakdown(duplicateEntries));
    }

    /// <summary>연결 경로 ID와 재료 역할이 맞지 않는 원장 항목을 거부합니다.</summary>
    [Fact]
    public void LengthLedgerEntry_RequiresIdKindToMatchConductorRole()
    {
        Assert.Throws<ArgumentException>(() => new LengthLedgerEntry(
            HeatingPathSegmentId.New(), null, LengthContributionKind.SeparateConnectionWire,
            GeometryDomain.External, new LengthMm(10), default));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LengthLedgerEntry(
            HeatingPathSegmentId.New(), null, LengthContributionKind.HeatingBody,
            GeometryDomain.OnMold, new LengthMm(-1), default));
    }

    /// <summary>도체 사양은 양의 제원을 요구하고 Supply 누락은 nullable 상태로 보존합니다.</summary>
    [Fact]
    public void WireSpecification_ValidatesPhysicalValuesAndSupplyKeepsMissingInputs()
    {
        var wire = new WireSpecification("SUS 316L", new LengthMm(0.3), new ResistanceOhmsPerMeter(4));
        var incompleteSupply = new Supply(actualVoltageVrms: null, maximumVoltageVrms: new VoltageVrms(220),
            maximumCurrentAmps: new CurrentAmps(16));

        Assert.Equal("SUS 316L", wire.MaterialName);
        Assert.Equal(0.3, wire.DiameterMm.Value);
        Assert.Null(incompleteSupply.ActualVoltageVrms);
        Assert.Null(incompleteSupply.MaximumPowerWatts);
        Assert.Throws<ArgumentOutOfRangeException>(() => new WireSpecification(
            "SUS 316L", default, new ResistanceOhmsPerMeter(4)));
    }
}
