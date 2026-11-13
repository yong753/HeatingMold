using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HeatingMold.Core.Geometry;

namespace HeatingMold.App;

/// <summary>
/// 간격 분석 결과의 동일성을 판정하는 지문입니다. 모델·작업 컨텍스트·프레임·영역/조각 identity·
/// 확정 당시 간격/직경/방향·전체 좌표(중간점 포함)를 모두 포함하므로, 좌표가 하나라도 달라지면
/// 같은 지문으로 재사용되지 않습니다. 표시 전용이며 경로·길이·전기 판정에 쓰지 않습니다.
/// </summary>
internal static class SpacingAnalysisFingerprint
{
    internal const string PolicyVersion = "spacing-v3";

    internal static string Build(
        long modelRevision,
        long contextId,
        long contextRevision,
        ProjectionFrame? frame,
        IReadOnlyList<SpacingRunEntry>? runs)
    {
        var builder = new StringBuilder(256);
        builder.Append(PolicyVersion);
        builder.Append("|model:").Append(modelRevision.ToString(CultureInfo.InvariantCulture));
        builder.Append("|context:")
            .Append(contextId.ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(contextRevision.ToString(CultureInfo.InvariantCulture));

        if (frame == null)
        {
            builder.Append("|frame:none");
        }
        else
        {
            builder.Append("|frame:");
            AppendVec3(builder, frame.Origin);
            builder.Append(',');
            AppendVec3(builder, frame.AxisX);
            builder.Append(',');
            AppendVec3(builder, frame.AxisY);
            builder.Append(',');
            AppendVec3(builder, frame.Normal);
        }

        if (runs == null || runs.Count == 0)
        {
            builder.Append("|runs:none");
            return builder.ToString();
        }

        var ordered = new List<SpacingRunEntry>(runs);
        ordered.Sort(static (left, right) =>
        {
            int byArea = string.CompareOrdinal(left.AreaId, right.AreaId);
            return byArea != 0 ? byArea : left.PieceIndex.CompareTo(right.PieceIndex);
        });

        foreach (SpacingRunEntry run in ordered)
        {
            builder.Append("|run:")
                .Append(run.AreaId)
                .Append(':')
                .Append(run.PieceIndex.ToString(CultureInfo.InvariantCulture))
                .Append(':')
                .Append(run.DirectionDeg.ToString("R", CultureInfo.InvariantCulture))
                .Append(':')
                .Append(run.ConfiguredGapMm.ToString("R", CultureInfo.InvariantCulture))
                .Append(':')
                .Append(run.WireDiameterMm.ToString("R", CultureInfo.InvariantCulture))
                .Append(':')
                .Append(run.R.ToString(CultureInfo.InvariantCulture))
                .Append(',')
                .Append(run.G.ToString(CultureInfo.InvariantCulture))
                .Append(',')
                .Append(run.B.ToString(CultureInfo.InvariantCulture));

            for (int index = 0; index < run.Points.Count; index++)
            {
                Vec2 point = run.Points[index];
                builder.Append(';')
                    .Append(point.X.ToString("R", CultureInfo.InvariantCulture))
                    .Append(',')
                    .Append(point.Y.ToString("R", CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    private static void AppendVec3(StringBuilder builder, Vec3 value)
    {
        builder.Append(value.X.ToString("R", CultureInfo.InvariantCulture))
            .Append(',')
            .Append(value.Y.ToString("R", CultureInfo.InvariantCulture))
            .Append(',')
            .Append(value.Z.ToString("R", CultureInfo.InvariantCulture));
    }
}
