using System.Globalization;
using HeatingMold.Application.Ports.Export;
using HeatingMold.Core.Area;
using HeatingMold.Core.Circuits;
using HeatingMold.Core.Electrical;
using HeatingMold.Core.Primitives;
using HeatingMold.Electrical.Area;

namespace HeatingMold.Infrastructure.Export;

/// <summary>확정 snapshot에서 DXF 사양표 행과 설명 문구를 만듭니다.</summary>
public sealed class SpecificationDataBuilder
{
    private static readonly IReadOnlyList<string> Headers = Array.AsReadOnly(
        new[] { "Circuit", "Item", "Value", "Unit / status" });

    /// <summary>회로별 3차원 길이, 전기 판정과 적용면적을 안정된 순서로 반환합니다.</summary>
    public DxfSpecificationTable Build(ExportSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var rows = new List<DxfSpecificationRow>();
        AddGeneralRows(snapshot, rows);

        Dictionary<CircuitId, ElectricalResult> resultsByCircuit = snapshot.ElectricalResults
            .ToDictionary(result => result.CircuitId);

        foreach (Circuit circuit in snapshot.Project.Circuits
                     .OrderBy(circuit => circuit.Sequence)
                     .ThenBy(circuit => circuit.Id.Value))
        {
            resultsByCircuit.TryGetValue(circuit.Id, out ElectricalResult? result);
            string circuitName = $"C{circuit.Sequence + 1}";
            AddCircuitRows(snapshot, circuit, circuitName, result, rows);
        }

        if (snapshot.Project.Circuits.Count == 0)
        {
            rows.Add(new DxfSpecificationRow("—", "Circuit status", "Unavailable", "No circuits"));
        }

        string[] notes =
        [
            "DXF geometry is an orthographic projection in the working coordinate system.",
            "Specification lengths are measured in 3D; projected DXF lengths are not used for electrical calculations.",
            "Resistance length includes heating wire only. Separate connection wire is excluded.",
            "Applied area is allocated mould-surface area, not projected drawing area.",
            "Unavailable values indicate missing or unevaluated inputs; they are not zero or a passing result."
        ];

        return new DxfSpecificationTable(
            Headers,
            Array.AsReadOnly(rows.ToArray()),
            Array.AsReadOnly(notes));
    }

    private static void AddGeneralRows(ExportSnapshot snapshot, ICollection<DxfSpecificationRow> rows)
    {
        rows.Add(new DxfSpecificationRow("General", "Project", snapshot.Project.Name, ""));
        rows.Add(new DxfSpecificationRow(
            "General",
            "Design revision",
            snapshot.Project.Revision.Value.ToString(CultureInfo.InvariantCulture),
            "revision"));
        rows.Add(new DxfSpecificationRow(
            "General",
            "Source SHA-256",
            snapshot.Project.SourceModel.SourceSha256,
            "SHA-256"));

        if (snapshot.Project.WireSpecification is { } wire)
        {
            rows.Add(new DxfSpecificationRow("General", "Wire material", wire.MaterialName, ""));
            rows.Add(new DxfSpecificationRow(
                "General",
                "Wire diameter",
                Format(wire.DiameterMm.Value, 2),
                "mm"));
            rows.Add(new DxfSpecificationRow(
                "General",
                "Unit resistance",
                Format(wire.ResistanceOhmsPerMeter.Value, 3),
                "ohm/m"));
        }
        else
        {
            rows.Add(new DxfSpecificationRow("General", "Wire specification", "Unavailable", "Not entered"));
        }

        if (snapshot.AreaAllocation is { } allocation)
        {
            rows.Add(new DxfSpecificationRow(
                "Area",
                "Effective surface area",
                Format(allocation.TotalEffectiveSurfaceAreaMm2.Value, 2),
                "mm2"));
            rows.Add(new DxfSpecificationRow(
                "Area",
                "Allocated area",
                Format(allocation.TotalAppliedAreaMm2.Value, 2),
                "mm2"));
            rows.Add(new DxfSpecificationRow(
                "Area",
                "Unassigned area",
                Format(allocation.UnassignedAppliedAreaMm2.Value, 2),
                "mm2"));
            rows.Add(new DxfSpecificationRow(
                "Area",
                "Allocation quality",
                allocation.Quality.IsConverged ? "Converged" : "Approximate",
                allocation.Quality.RuleVersion));
        }
        else
        {
            rows.Add(new DxfSpecificationRow("Area", "Applied area", "Unavailable", "Not calculated"));
        }

        if (snapshot.ElectricalAreaResult is { } electricalArea)
        {
            rows.Add(new DxfSpecificationRow(
                "Electrical area",
                "Aggregation status",
                electricalArea.Status.ToString(),
                string.Join(", ", electricalArea.Issues)));
            AddAreaValue(rows, "Electrical area", "Compliant applied area", electricalArea.ElectricallyCompliantAppliedAreaMm2);
            AddAreaValue(rows, "Electrical area", "Non-compliant applied area", electricalArea.NonCompliantAppliedAreaMm2);
            AddAreaValue(rows, "Electrical area", "Unevaluated applied area", electricalArea.UnevaluatedAppliedAreaMm2);
        }
        else
        {
            rows.Add(new DxfSpecificationRow("Electrical area", "Aggregation", "Unavailable", "Not calculated"));
        }
    }

    private static void AddCircuitRows(
        ExportSnapshot snapshot,
        Circuit circuit,
        string circuitName,
        ElectricalResult? result,
        ICollection<DxfSpecificationRow> rows)
    {
        rows.Add(new DxfSpecificationRow(circuitName, "Stable circuit ID", circuit.Id.Value.ToString("N"), "Guid"));

        LengthBreakdown? lengths = result?.LengthBreakdown;
        AddLengthRow(rows, circuitName, "Heating body", lengths?.HeatingBodyMm, result);
        AddLengthRow(rows, circuitName, "Heating extension", lengths?.HeatingExtensionMm, result);
        AddLengthRow(rows, circuitName, "Heating wire allowance", lengths?.HeatingExtraAllowanceMm, result);
        AddLengthRow(rows, circuitName, "Separate connection wire", lengths?.SeparateConnectionWireMm, result);
        AddLengthRow(rows, circuitName, "Separate connection allowance", lengths?.SeparateConnectionWireExtraAllowanceMm, result);

        if (lengths is not null)
        {
            double connectionTotalMm = lengths.HeatingExtensionMm.Value +
                lengths.HeatingExtraAllowanceMm.Value +
                lengths.SeparateConnectionWireMm.Value +
                lengths.SeparateConnectionWireExtraAllowanceMm.Value;
            rows.Add(new DxfSpecificationRow(
                circuitName,
                "Total connection length",
                Format(connectionTotalMm, 2),
                "mm"));
            rows.Add(new DxfSpecificationRow(
                circuitName,
                "Resistance length",
                Format(lengths.ResistanceLengthMm.Value / 1000, 3),
                "m"));
        }
        else
        {
            rows.Add(new DxfSpecificationRow(circuitName, "Total connection length", "Unavailable", "Not calculated"));
            rows.Add(new DxfSpecificationRow(circuitName, "Resistance length", "Unavailable", "Not calculated"));
        }

        AddMeasuredValue(rows, circuitName, "Resistance", result?.ResistanceOhms?.Value, "ohm", 3, result);
        AddMeasuredValue(
            rows,
            circuitName,
            "Actual voltage",
            result?.ActualVoltageVrms?.Value ?? circuit.Supply.ActualVoltageVrms?.Value,
            "V RMS",
            2,
            result,
            circuit.Supply.ActualVoltageVrms.HasValue ? "Input" : "Missing input");
        AddMeasuredValue(rows, circuitName, "Current", result?.CurrentAmps?.Value, "A", 3, result);
        AddMeasuredValue(rows, circuitName, "Power", result?.PowerWatts?.Value, "W", 2, result);

        foreach (ElectricalLimitKind kind in Enum.GetValues<ElectricalLimitKind>())
            AddLimitRow(rows, circuitName, circuit, result, kind);

        rows.Add(new DxfSpecificationRow(
            circuitName,
            "Electrical status",
            result?.Status.ToString() ?? "Not calculated",
            result?.FailureCode ?? (result is null ? "Unavailable" : "")));

        if (result is not null)
        {
            foreach (string issue in result.InputIssues)
                rows.Add(new DxfSpecificationRow(circuitName, "Input issue", issue, ""));
        }

        AreaMm2 circuitArea = default;
        bool hasCircuitArea = snapshot.AreaAllocation is { } allocation &&
            allocation.CircuitAppliedAreas.TryGetValue(circuit.Id, out circuitArea);
        if (hasCircuitArea)
        {
            rows.Add(new DxfSpecificationRow(
                circuitName,
                "Applied area",
                Format(circuitArea.Value, 2),
                "mm2"));
        }
        else
        {
            rows.Add(new DxfSpecificationRow(
                circuitName,
                "Applied area",
                "Unavailable",
                snapshot.AreaAllocation is null ? "Not calculated" : "No allocation"));
        }
    }

    private static void AddLengthRow(
        ICollection<DxfSpecificationRow> rows,
        string circuitName,
        string item,
        LengthMm? length,
        ElectricalResult? result)
    {
        rows.Add(new DxfSpecificationRow(
            circuitName,
            item,
            length.HasValue ? Format(length.Value.Value, 2) : "Unavailable",
            length.HasValue ? "mm" : result is null ? "Not calculated" : "Unavailable"));
    }

    private static void AddMeasuredValue(
        ICollection<DxfSpecificationRow> rows,
        string circuitName,
        string item,
        double? value,
        string unit,
        int decimalPlaces,
        ElectricalResult? result,
        string? missingStatus = null)
    {
        string status = value.HasValue
            ? unit
            : missingStatus ?? (result?.Status.ToString() ?? "Not calculated");
        rows.Add(new DxfSpecificationRow(
            circuitName,
            item,
            value.HasValue ? Format(value.Value, decimalPlaces) : "Unavailable",
            status));
    }

    private static void AddLimitRow(
        ICollection<DxfSpecificationRow> rows,
        string circuitName,
        Circuit circuit,
        ElectricalResult? result,
        ElectricalLimitKind kind)
    {
        ElectricalLimitResult? limit = result?.Limits.FirstOrDefault(candidate => candidate.Kind == kind);
        double? measured = limit?.MeasuredValue;
        double? allowed = limit?.AllowedValue ?? GetSupplyLimit(circuit.Supply, kind);
        string item = kind switch
        {
            ElectricalLimitKind.MaximumVoltage => "Maximum voltage",
            ElectricalLimitKind.MaximumCurrent => "Maximum current",
            ElectricalLimitKind.MaximumPower => "Maximum power",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        string unit = limit?.UnitSymbol ?? GetLimitUnit(kind);
        string value = $"measured {FormatOptional(measured)}; limit {FormatOptional(allowed)}";
        string status = limit?.Status.ToString() ?? (result is null ? "Not calculated" : "Unavailable");
        rows.Add(new DxfSpecificationRow(circuitName, item, value, $"{unit} / {status}"));
    }

    private static double? GetSupplyLimit(Supply supply, ElectricalLimitKind kind) => kind switch
    {
        ElectricalLimitKind.MaximumVoltage => supply.MaximumVoltageVrms?.Value,
        ElectricalLimitKind.MaximumCurrent => supply.MaximumCurrentAmps?.Value,
        ElectricalLimitKind.MaximumPower => supply.MaximumPowerWatts?.Value,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static string GetLimitUnit(ElectricalLimitKind kind) => kind switch
    {
        ElectricalLimitKind.MaximumVoltage => "V RMS",
        ElectricalLimitKind.MaximumCurrent => "A",
        ElectricalLimitKind.MaximumPower => "W",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static void AddAreaValue(
        ICollection<DxfSpecificationRow> rows,
        string scope,
        string item,
        AreaMm2? area)
    {
        rows.Add(new DxfSpecificationRow(
            scope,
            item,
            area.HasValue ? Format(area.Value.Value, 2) : "Unavailable",
            area.HasValue ? "mm2" : "Not evaluated"));
    }

    private static string FormatOptional(double? value) => value.HasValue ? Format(value.Value, 2) : "unavailable";

    private static string Format(double value, int decimalPlaces) =>
        value.ToString($"F{decimalPlaces}", CultureInfo.InvariantCulture);
}

/// <summary>DXF 사양표의 제목 행, 데이터 행과 설명 문구입니다.</summary>
public sealed record DxfSpecificationTable(
    IReadOnlyList<string> Headers,
    IReadOnlyList<DxfSpecificationRow> Rows,
    IReadOnlyList<string> Notes);

/// <summary>DXF 사양표에서 하나의 편집 가능한 셀 행을 나타냅니다.</summary>
public sealed record DxfSpecificationRow(string Scope, string Item, string Value, string UnitOrStatus);
