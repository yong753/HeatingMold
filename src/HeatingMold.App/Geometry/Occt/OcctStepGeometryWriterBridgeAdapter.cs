using System;
using System.IO;
using System.Linq;
using System.Threading;
using HeatingMold.Application.Ports.Geometry;
using HeatingMold.Infrastructure.Export;
using HeatingMold.Infrastructure.Geometry.Occt;
using HeatingMold.OccBridge;

namespace HeatingMold.App.Geometry.Occt;

/// <summary>Infrastructure STEP wire DTO를 OccBridge의 OCCT writer API에 연결합니다.</summary>
internal sealed class OcctStepGeometryWriterBridgeAdapter : IStepGeometryWriterBridge
{
    /// <summary>필요할 때 원본 STEP 바이트를 임시 입력으로 준비하고 회로별 wire를 전송합니다.</summary>
    public StepGeometryWriteResult Write(
        string outputPath,
        ModelSource sourceModel,
        System.Collections.Generic.IReadOnlyList<StepDesignGroup> groups,
        bool includeSourceModel,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(outputPath) || sourceModel is null || groups is null || groups.Any(group => group is null))
            return new StepGeometryWriteResult(StepGeometryWriteStatus.InvalidInput, "STEP 출력 경로나 그룹 입력이 유효하지 않습니다.");
        if (includeSourceModel && sourceModel.LengthBytes == 0)
            return new StepGeometryWriteResult(StepGeometryWriteStatus.SourceModelReadFailed, "출력에 포함할 원본 STEP 바이트가 비어 있습니다.");

        string? sourcePath = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (includeSourceModel)
            {
                sourcePath = Path.Combine(Path.GetTempPath(), $"HeatingMold-{Guid.NewGuid():N}.step");
                using var sourceFile = new FileStream(
                    sourcePath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 81_920,
                    FileOptions.SequentialScan);
                sourceModel.CopyTo(sourceFile, cancellationToken);
                sourceFile.Flush(flushToDisk: true);
            }

            StepGeometryGroupData[] nativeGroups = groups.Select(ToNativeGroup).ToArray();
            StepDesignWriteResult nativeResult = Bridge.WriteDesignStep(
                outputPath,
                sourcePath,
                includeSourceModel,
                nativeGroups);
            cancellationToken.ThrowIfCancellationRequested();
            if (nativeResult is null)
                return new StepGeometryWriteResult(StepGeometryWriteStatus.GeometryFailure, "OccBridge가 STEP 출력 결과를 반환하지 않았습니다.");

            return new StepGeometryWriteResult(MapStatus(nativeResult.Status), nativeResult.Diagnostic);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new StepGeometryWriteResult(StepGeometryWriteStatus.GeometryFailure, exception.Message);
        }
        finally
        {
            if (sourcePath is not null && File.Exists(sourcePath))
            {
                try
                {
                    File.Delete(sourcePath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // 임시 입력 정리 실패가 Native 출력 결과를 덮지 않도록 둡니다.
                }
            }
        }
    }

    private static StepGeometryGroupData ToNativeGroup(StepDesignGroup group) => new()
    {
        Name = group.Name,
        Wires = group.Wires.Select(wire => new StepDesignWireData
        {
            Name = wire.Name,
            Curves = wire.Curves.Select(curve => new StepDesignCurveData
            {
                Definition = ToNativeCurve(curve.Definition),
                IsReversed = curve.IsReversed
            }).ToArray()
        }).ToArray()
    };

    private static CurveDefinitionData ToNativeCurve(CurveDefinitionBridgeDto curve) => new()
    {
        Kind = curve.Kind,
        Direction = curve.Direction,
        Start = curve.Start?.ToArray(),
        End = curve.End?.ToArray(),
        Points = curve.Points?.ToArray(),
        Frame = curve.Frame?.ToArray(),
        Center = curve.Center?.ToArray(),
        RadiusMm = curve.RadiusMm ?? 0,
        StartAngleRad = curve.StartAngleRad ?? 0,
        EndAngleRad = curve.EndAngleRad ?? 0,
        Degree = curve.Degree ?? 0,
        Poles = curve.Poles?.ToArray(),
        Weights = curve.Weights?.ToArray(),
        Knots = curve.Knots?.ToArray(),
        Multiplicities = curve.Multiplicities?.ToArray(),
        IsPeriodic = curve.IsPeriodic ?? false,
        TrimStart = curve.TrimStart ?? 0,
        TrimEnd = curve.TrimEnd ?? 0
    };

    private static StepGeometryWriteStatus MapStatus(StepDesignWriteStatus status) => status switch
    {
        StepDesignWriteStatus.Success => StepGeometryWriteStatus.Success,
        StepDesignWriteStatus.InvalidInput => StepGeometryWriteStatus.InvalidInput,
        StepDesignWriteStatus.SourceModelReadFailed => StepGeometryWriteStatus.SourceModelReadFailed,
        StepDesignWriteStatus.CurveDefinitionInvalid => StepGeometryWriteStatus.CurveDefinitionInvalid,
        StepDesignWriteStatus.WireBuildFailed => StepGeometryWriteStatus.WireBuildFailed,
        StepDesignWriteStatus.WriteFailed => StepGeometryWriteStatus.WriteFailed,
        _ => StepGeometryWriteStatus.GeometryFailure
    };
}
