#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HeatingMold.Application.Sessions;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Design.Quality;

namespace HeatingMold.App;

/// <summary>확정된 간격·표면 커버리지 관측값을 화면 표시용 데이터로 바꿉니다.</summary>
internal static class CenterPitchQualityPresentation
{
    /// <summary>현재 확정 snapshot에서 상태 문구와 영역별 요약을 만듭니다.</summary>
    public static CenterPitchQualityPanelData Build(SessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        DesignProject? project = snapshot.CurrentProject;
        if (project is null)
        {
            return new CenterPitchQualityPanelData(
                "확정 설계의 간격·표면 커버리지 관측 결과가 없습니다.",
                Array.Empty<CenterPitchQualityDisplayRow>());
        }

        if (snapshot.CenterPitchQualityReports.Count == 0)
        {
            return new CenterPitchQualityPanelData(
                "활성 발열 경로가 없거나 표면 품질 측정이 완료되지 않았습니다.",
                Array.Empty<CenterPitchQualityDisplayRow>());
        }

        DependencyStamp? currentStamp = snapshot.Geometry?.DependencyStamp;
        CenterPitchQualityDisplayRow[] rows = snapshot.CenterPitchQualityReports
            .OrderBy(report => RegionOrder(project, report.RegionId))
            .Select(report => BuildRegionRow(project, report, currentStamp, snapshot.IsPresentationOutOfSync))
            .ToArray();
        bool hasStaleReport = rows.Any(row => row.IsStale);
        string status = hasStaleReport
            ? "일부 값은 현재 확정 형상 또는 화면과 일치하지 않는 이전 측정입니다."
            : "표본 기반 관측값이며 열적 합격 판정은 아닙니다.";

        return new CenterPitchQualityPanelData(status, rows);
    }

    private static CenterPitchQualityDisplayRow BuildRegionRow(
        DesignProject project,
        CenterPitchQualityReport report,
        DependencyStamp? currentStamp,
        bool isPresentationOutOfSync)
    {
        int regionIndex = RegionOrder(project, report.RegionId);
        string label = regionIndex > 0
            ? $"영역 {regionIndex}"
            : $"영역 {report.RegionId.Value.ToString("N")[..8]}";
        bool isStale = currentStamp is null ||
            report.DependencyStamp != currentStamp ||
            isPresentationOutOfSync;
        if (isStale)
        {
            return new CenterPitchQualityDisplayRow(
                label,
                "이전 측정: 현재 확정 형상에 적용할 수 없습니다.",
                string.Empty,
                null,
                Array.Empty<CenterPitchQualityLanePitchDisplayRow>(),
                true);
        }

        SurfacePathDistanceSample? farthestSample = report.SurfaceSamples
            .OrderByDescending(sample => sample.CenterlineDistanceMm)
            .FirstOrDefault();
        string coverage =
            $"중심선 거리 최대 {Format(report.MaximumSampledCenterlineDistanceMm)} mm · " +
            $"오차 상한 포함 {Format(report.MaximumSampledCenterlineDistanceUpperBoundMm)} mm · " +
            $"표본 {report.SurfaceProbeCount.ToString("N0", CultureInfo.InvariantCulture)}개";
        string location = farthestSample is null
            ? "최대 거리 표본 위치가 없습니다."
            : $"최대 거리 위치: {FormatPoint(farthestSample.Location)} mm";
        CenterPitchQualityLocationSelection? coverageSelection = farthestSample is null
            ? null
            : new CenterPitchQualityLocationSelection(report.RegionId, report.DependencyStamp, farthestSample.Location);

        CenterPitchQualityLanePitchDisplayRow[] laneRows = report.LanePitchMeasurements
            .OrderByDescending(measurement => Math.Abs(measurement.AverageDeviationFromTargetMm ?? measurement.MaximumChordDistanceMm - measurement.MinimumChordDistanceMm))
            .Take(3)
            .Select(measurement => new CenterPitchQualityLanePitchDisplayRow(
                FormatLanePitch(measurement),
                new CenterPitchQualityLocationSelection(
                    report.RegionId,
                    report.DependencyStamp,
                    measurement.MaximumDistanceLocation)))
            .ToArray();
        return new CenterPitchQualityDisplayRow(label, coverage, location, coverageSelection, laneRows, false);
    }

    /// <summary>선택 위치가 현재 확정 형상과 일치하는 보고서에 속하는지 확인합니다.</summary>
    public static bool TryResolveCurrentLocation(
        SessionSnapshot snapshot,
        CenterPitchQualityLocationSelection selection,
        out Vec3 location)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        location = default;
        if (snapshot.IsPresentationOutOfSync ||
            snapshot.CurrentProject is null ||
            snapshot.Geometry is null ||
            snapshot.Geometry.DependencyStamp != selection.DependencyStamp)
        {
            return false;
        }

        CenterPitchQualityReport? report = snapshot.CenterPitchQualityReports.FirstOrDefault(candidate =>
            candidate.RegionId == selection.RegionId &&
            candidate.DependencyStamp == selection.DependencyStamp);
        if (report is null || !ContainsLocation(report, selection.Location))
            return false;

        location = selection.Location;
        return true;
    }

    /// <summary>표시할 좌표가 현재 보고서가 보관한 표면 또는 레인 표본인지 확인합니다.</summary>
    private static bool ContainsLocation(CenterPitchQualityReport report, Vec3 location) =>
        report.SurfaceSamples.Any(sample => sample.Location == location) ||
        report.LanePitchMeasurements.Any(measurement => measurement.MaximumDistanceLocation == location);

    private static string FormatLanePitch(LanePitchMeasurement measurement)
    {
        string targetDeviation = measurement.TargetCenterPitchMm.HasValue && measurement.AverageDeviationFromTargetMm.HasValue
            ? $" · 목표 {Format(measurement.TargetCenterPitchMm.Value.Value)} mm 대비 " +
              $"{FormatSigned(measurement.AverageDeviationFromTargetMm.Value)} mm"
            : string.Empty;
        return $"레인 {measurement.FirstLaneIndex}–{measurement.SecondLaneIndex}: " +
            $"표본 간격 {Format(measurement.MinimumChordDistanceMm)}–{Format(measurement.MaximumChordDistanceMm)} mm, " +
            $"평균 {Format(measurement.AverageChordDistanceMm)} mm{targetDeviation} · " +
            $"오차 ±{Format(measurement.ApproximationBoundMm)} mm · " +
            $"위치 {FormatPoint(measurement.MaximumDistanceLocation)} mm";
    }

    private static int RegionOrder(DesignProject project, RegionId regionId)
    {
        for (int index = 0; index < project.HeatingRegions.Count; index++)
        {
            if (project.HeatingRegions[index].Id == regionId)
                return index + 1;
        }

        return int.MaxValue;
    }

    private static string Format(double value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string FormatSigned(double value) => value.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture);

    private static string FormatPoint(HeatingMold.Core.Geometry.Vec3 point) =>
        $"({Format(point.X)}, {Format(point.Y)}, {Format(point.Z)})";
}

/// <summary>품질 패널의 안내 문구와 영역별 관측 요약을 보관합니다.</summary>
internal sealed record CenterPitchQualityPanelData(
    string Status,
    IReadOnlyList<CenterPitchQualityDisplayRow> Rows);

/// <summary>품질 표본 위치를 표시하고 현재 형상 위치 선택값을 보관합니다.</summary>
internal sealed record CenterPitchQualityDisplayRow(
    string RegionLabel,
    string CoverageSummary,
    string CoverageLocation,
    CenterPitchQualityLocationSelection? CoverageLocationSelection,
    IReadOnlyList<CenterPitchQualityLanePitchDisplayRow> LanePitchRows,
    bool IsStale)
{
    /// <summary>표면 표본 위치를 뷰포트에서 표시할 수 있는지 나타냅니다.</summary>
    public bool CanShowCoverageLocation => CoverageLocationSelection is not null && !IsStale;
}

/// <summary>간격 표본 설명과 뷰포트에서 선택할 위치를 보관합니다.</summary>
internal sealed record CenterPitchQualityLanePitchDisplayRow(
    string Text,
    CenterPitchQualityLocationSelection Selection)
{
    /// <summary>이 간격 표본 위치를 뷰포트에서 표시할 수 있는지 나타냅니다.</summary>
    public bool CanShowLocation => Selection is not null;
}

/// <summary>형상 지문과 영역을 함께 묶어 오래된 위치 선택을 차단합니다.</summary>
internal sealed record CenterPitchQualityLocationSelection(
    RegionId RegionId,
    DependencyStamp DependencyStamp,
    Vec3 Location);
