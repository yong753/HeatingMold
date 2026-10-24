using System;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows;
using HeatingMold.App;
using HeatingMold.App.ViewModels;
using HeatingMold.Core.Circuits;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Primitives;

namespace HeatingMold.App.Tests;

/// <summary>연결 경로 편집 표가 확정 Native 측정값과 경로 역할을 보존하는지 검사합니다.</summary>
public sealed class ConnectionPathEditingTests
{
    /// <summary>표의 좌표는 저장 곡선이 아니라 현재 측정 곡선 끝점에서 가져옵니다.</summary>
    [Fact]
    public void CreateConnectionPathLegRows_UsesMeasuredEndpointsAndPreservesSegmentRules()
    {
        ConnectionPointId connectionPointId = ConnectionPointId.New();
        ConnectionPathSegmentId onMoldSegmentId = ConnectionPathSegmentId.New();
        ConnectionPathSegmentId externalSegmentId = ConnectionPathSegmentId.New();
        Vec3 heatingEndpoint = new(0, 0, 0);
        Vec3 surfaceAnchor = new(4, 0, 0);
        Vec3 externalEndpoint = new(9, 0, 0);
        var onMoldCurve = new LineCurveDefinition(heatingEndpoint, surfaceAnchor);
        var externalCurve = new LineCurveDefinition(surfaceAnchor, externalEndpoint);
        var path = new ConnectionPath(
            ConnectionPathId.New(),
            connectionPointId,
            [
                new ConnectionPathSegment(
                    onMoldSegmentId,
                    0,
                    ConductorRole.HeatingWireExtension,
                    GeometryDomain.OnMold,
                    new LengthMm(1.25),
                    onMoldCurve),
                new ConnectionPathSegment(
                    externalSegmentId,
                    1,
                    ConductorRole.SeparateConnectionWire,
                    GeometryDomain.External,
                    new LengthMm(2.5),
                    externalCurve,
                    isMoldExitTransition: true)
            ]);
        var geometry = new GeometrySnapshot(
            GeometryDocumentId.New(),
            new ModelRevision(3),
            CreateDependencyStamp(),
            [
                CreateMeasuredCurve(onMoldSegmentId, onMoldCurve, heatingEndpoint, surfaceAnchor, 4),
                CreateMeasuredCurve(externalSegmentId, externalCurve, surfaceAnchor, externalEndpoint, 5)
            ]);

        ConnectionPathLegRow[] rows = ConnectionPathEditorViewModel.CreateRows(path, geometry);

        Assert.Equal(2, rows.Length);
        Assert.Equal(surfaceAnchor, new Vec3(rows[0].X, rows[0].Y, rows[0].Z));
        Assert.Equal(externalEndpoint, new Vec3(rows[1].X, rows[1].Y, rows[1].Z));
        Assert.Equal(GeometryDomain.OnMold, rows[0].GeometryDomain);
        Assert.Equal(ConductorRole.HeatingWireExtension, rows[0].ConductorRole);
        Assert.Equal(1.25, rows[0].AdditionalAllowanceMm);
        Assert.Equal(GeometryDomain.External, rows[1].GeometryDomain);
        Assert.Equal(ConductorRole.SeparateConnectionWire, rows[1].ConductorRole);
        Assert.Equal(2.5, rows[1].AdditionalAllowanceMm);
        Assert.True(rows[1].IsMoldExitTransition);
    }

    /// <summary>기본 외부 구간을 나눌 때 이탈 표식을 새 첫 외부 구간으로 옮깁니다.</summary>
    [Fact]
    public void AddAndRemoveWaypoint_PreservesSingleMoldExitTransition()
    {
        WpfTestThread.Run(() =>
        {
            var window = new MainWindow();
            ObservableCollection<ConnectionPathLegRow> rows = GetLegRows(window);
            rows.Add(new ConnectionPathLegRow(
                new Vec3(0, 0, 0),
                GeometryDomain.OnMold,
                ConductorRole.HeatingWireExtension,
                additionalAllowanceMm: 0,
                isMoldExitTransition: false));
            rows.Add(new ConnectionPathLegRow(
                new Vec3(8, 0, 0),
                GeometryDomain.External,
                ConductorRole.SeparateConnectionWire,
                additionalAllowanceMm: 0,
                isMoldExitTransition: true));

            InvokePrivate(window, "AddConnectionPathWaypoint_Click");

            Assert.Equal(3, rows.Count);
            Assert.Equal(new Vec3(4, 0, 0), new Vec3(rows[1].X, rows[1].Y, rows[1].Z));
            Assert.True(rows[1].IsMoldExitTransition);
            Assert.False(rows[2].IsMoldExitTransition);

            window.ConnectionPathLegsGrid.SelectedItem = rows[1];
            InvokePrivate(window, "RemoveConnectionPathWaypoint_Click");

            Assert.Equal(2, rows.Count);
            Assert.True(rows[1].IsMoldExitTransition);
            Assert.Equal("선택한 내부 경유점을 제거했습니다.", window.ConnectionPathEditorStatusText.Text);
        });
    }

    /// <summary>창에서 편집 행 collection을 읽어 같은 WPF 이벤트 경로에 전달합니다.</summary>
    private static ObservableCollection<ConnectionPathLegRow> GetLegRows(MainWindow window)
    {
        FieldInfo field = typeof(MainWindow).GetField(
            "_mainWindowViewModel",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(MainWindow).FullName, "_mainWindowViewModel");
        var viewModel = (MainWindowViewModel)field.GetValue(window);
        return viewModel.ConnectionPaths.Legs;
    }

    /// <summary>연결 경로 편집 이벤트 처리기를 테스트 창에서 호출합니다.</summary>
    private static void InvokePrivate(MainWindow window, string methodName)
    {
        MethodInfo method = typeof(MainWindow).GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(MainWindow).FullName, methodName);
        method.Invoke(window, [window, new RoutedEventArgs()]);
    }

    /// <summary>완료된 기하 표본과 검증용 revision 지문으로 측정 곡선을 만듭니다.</summary>
    private static CurveGeometrySnapshot CreateMeasuredCurve(
        ConnectionPathSegmentId segmentId,
        CurveDefinition definition,
        Vec3 start,
        Vec3 end,
        double lengthMm) => new(
        null,
        segmentId,
        definition,
        new LengthMm(lengthMm),
        start,
        end,
        [start, end],
        new CurveApproximationInfo(
            requestedDeflectionMm: .05,
            requestedAngularToleranceRad: .1,
            maximumMeasuredDeviationMm: .01,
            maximumSampleSpacingMm: lengthMm,
            probeCount: 2,
            isComplete: true,
            ruleVersion: "test-measured-curve-v1"));

    /// <summary>기하 snapshot 단위 테스트에 사용할 유효한 SHA-256 지문을 구성합니다.</summary>
    private static DependencyStamp CreateDependencyStamp()
    {
        string hash = new('a', 64);
        return new DependencyStamp(
            new DesignRevision(7),
            hash,
            hash,
            hash,
            "test-dependency-v1");
    }
}
