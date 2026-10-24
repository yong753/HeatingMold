using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using HeatingMold.Application.Ports.Export;
using HeatingMold.Application.Sessions;
using HeatingMold.Core.Geometry;
using HeatingMold.OccBridge;
using Microsoft.Win32;

namespace HeatingMold.App;

public partial class MainWindow
{
    /// <summary>사용자가 선택한 위치에 금형 정사영 DXF를 저장합니다.</summary>
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (!HasCommittedGeometryModel())
        {
            StatusText.Text = "정사영 DXF를 저장하려면 STEP 모델을 먼저 여세요.";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "정사영 DXF 저장",
            Filter = "DXF 도면 (*.dxf)|*.dxf",
            DefaultExt = ".dxf",
            AddExtension = true,
            FileName = "mold_orthographic.dxf",
            OverwritePrompt = true
        };

        if (dialog.ShowDialog(this) == true)
            ExportOrthographicViewsToPath(dialog.FileName);
    }

    /// <summary>확정 모델의 HLR 정사영 네 장을 DXF 출력 유스케이스에 전달합니다.</summary>
    private void ExportOrthographicViewsToPath(string path)
    {
        if (!HasCommittedGeometryModel())
        {
            StatusText.Text = "정사영 DXF를 저장하려면 STEP 모델을 먼저 여세요.";
            return;
        }

        try
        {
            var result = _exportOrthographicViews.Execute(path, BuildViews());
            if (result.IsSuccess)
            {
                StatusText.Text = "DXF 저장 — " + result.GetValueOrThrow().Path;
                return;
            }

            string evidence = result.Issues
                .SelectMany(issue => issue.Evidence)
                .FirstOrDefault() ?? result.Issues.FirstOrDefault()?.Code ?? "알 수 없는 출력 오류";
            StatusText.Text = "DXF 저장 실패: " + evidence;
        }
        catch (Exception exception)
        {
            StatusText.Text = "DXF 저장 실패: " + exception.Message;
        }
    }

    /// <summary>확정 설계와 Native 문서가 있어 정사영을 조회할 수 있는지 확인합니다.</summary>
    private bool HasCommittedGeometryModel()
    {
        SessionSnapshot snapshot = _projectSession.Current;
        return snapshot.CurrentProject is not null &&
            snapshot.RuntimeDocument is not null &&
            snapshot.Geometry is not null;
    }

    /// <summary>정면·후면·상면·측면 순서로 HLR 정사영 도면을 구성합니다.</summary>
    private List<OrthographicDxfView> BuildViews()
    {
        double[] x = Bridge.GetAutoAxis(0);
        double[] y = Bridge.GetAutoAxis(1);
        double[] z = Bridge.GetAutoAxis(2);
        if (x is not { Length: >= 3 } ||
            y is not { Length: >= 3 } ||
            z is not { Length: >= 3 })
        {
            throw new InvalidOperationException("정사영 도면의 기준 축을 가져오지 못했습니다.");
        }

        return new List<OrthographicDxfView>
        {
            BuildHlrView(z[0], z[1], z[2], x[0], x[1], x[2]),
            BuildHlrView(-z[0], -z[1], -z[2], x[0], x[1], x[2]),
            BuildHlrView(-y[0], -y[1], -y[2], x[0], x[1], x[2]),
            BuildHlrView(x[0], x[1], x[2], y[0], y[1], y[2])
        };
    }

    /// <summary>Native HLR 선을 가시선과 은선의 DXF 다중선으로 나눕니다.</summary>
    private OrthographicDxfView BuildHlrView(
        double normalX,
        double normalY,
        double normalZ,
        double axisXX,
        double axisXY,
        double axisXZ)
    {
        var visibleEdges = new List<OrthographicDxfPolyline>();
        var hiddenEdges = new List<OrthographicDxfPolyline>();
        Vec3 xAxis = new Vec3(axisXX, axisXY, axisXZ).Normalized();

        foreach (HlrPolyline line in Bridge.HlrEdges(
                     normalX,
                     normalY,
                     normalZ,
                     xAxis.X,
                     xAxis.Y,
                     xAxis.Z,
                     includeHidden: true))
        {
            var points = new List<Vec2>(line.Points.Length / 2);
            for (int index = 0; index + 1 < line.Points.Length; index += 2)
                points.Add(new Vec2(line.Points[index], line.Points[index + 1]));

            if (points.Count < 2)
                continue;

            var polyline = new OrthographicDxfPolyline(points);
            if (line.Visible)
                visibleEdges.Add(polyline);
            else
                hiddenEdges.Add(polyline);
        }

        return new OrthographicDxfView(
            visibleEdges,
            hiddenEdges,
            Array.Empty<OrthographicDxfPolyline>(),
            Array.Empty<OrthographicDxfPolyline>());
    }
}
