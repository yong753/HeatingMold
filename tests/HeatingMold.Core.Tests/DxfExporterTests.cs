using HeatingMold.Core.Electrical;
using HeatingMold.Core.Geometry;
using HeatingMold.Export;
using netDxf;
using netDxf.Entities;

namespace HeatingMold.Core.Tests;

public class DxfExporterTests
{
    [Fact]
    public void ExportViews_WritesSelectableSpecTable()
    {
        var table = ElectricalSpecTableBuilder.Build(
            new[] { new ElectricalCircuitInput("A-1", 20000) },
            HeatingWireSpec.Case1, new PowerSupplySpec());

        string path = Path.Combine(Path.GetTempPath(),
            "hm_spec_" + Guid.NewGuid().ToString("N") + ".dxf");
        try
        {
            DxfExporter.ExportViews(path, Array.Empty<DxfView>(), specTable: table);

            DxfDocument doc = DxfDocument.Load(path);
            var texts = doc.Entities.All.OfType<Text>().Select(t => t.Value).ToList();

            Assert.Contains("전기 사양표", texts);
            Assert.Contains("A-1", texts);
            Assert.Contains(texts, t => t.Contains("80.00"));
            Assert.Contains(texts, t => t.Contains("2.75"));
            Assert.Contains(texts, t => t.Contains("605.0"));
            Assert.True(doc.Layers.Contains(DxfExporter.ElectricalSpecLayer));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ExportPlan_WritesHeatRouteWithoutRenormalizingCoordinates()
    {
        var table = ElectricalSpecTableBuilder.Build(
            new[] { new ElectricalCircuitInput("A-1", 20000) },
            HeatingWireSpec.Case1, new PowerSupplySpec());
        var view = new DxfView();
        view.VisibleEdges.Add(new DxfPolyline
        {
            Points = new[]
            {
                new Vec2(1000, 2000), new Vec2(1100, 2000), new Vec2(1100, 2050)
            }
        });
        view.HeatLines.Add(new DxfPolyline
        {
            Points = new[] { new Vec2(1010, 2010), new Vec2(1090, 2010) }
        });

        string path = Path.Combine(Path.GetTempPath(),
            "hm_plan_" + Guid.NewGuid().ToString("N") + ".dxf");
        try
        {
            Assert.True(DxfExporter.ExportPlan(path, view, table));

            DxfDocument doc = DxfDocument.Load(path);
            Polyline2D heat = Assert.Single(doc.Entities.All.OfType<Polyline2D>()
                .Where(p => p.Layer.Name == DxfExporter.HeatWireLayer));
            Assert.Equal(1010, heat.Vertexes[0].Position.X, 6);
            Assert.Equal(2010, heat.Vertexes[0].Position.Y, 6);
            Assert.Equal(1090, heat.Vertexes[1].Position.X, 6);
            Assert.Equal(2010, heat.Vertexes[1].Position.Y, 6);

            Text title = doc.Entities.All.OfType<Text>().Single(t => t.Value == "전기 사양표");
            Assert.Equal(1000, title.Position.X, 6);
            Assert.True(title.Position.Y < 2000, "표 제목이 평면도 아래에 있어야 합니다.");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ExportPlan_ScalesSpecTableWithMoldExtent()
    {
        var table = ElectricalSpecTableBuilder.Build(
            new[] { new ElectricalCircuitInput("A-1", 20000) },
            HeatingWireSpec.Case1, new PowerSupplySpec());
        var view = new DxfView();
        view.VisibleEdges.Add(new DxfPolyline
        {
            Points = new[]
            {
                new Vec2(0, 0), new Vec2(1200, 0), new Vec2(1200, 600), new Vec2(0, 600)
            }
        });
        view.HeatLines.Add(new DxfPolyline
        {
            Points = new[] { new Vec2(100, 100), new Vec2(1100, 100) }
        });

        string path = Path.Combine(Path.GetTempPath(),
            "hm_plan_scale_" + Guid.NewGuid().ToString("N") + ".dxf");
        try
        {
            Assert.True(DxfExporter.ExportPlan(path, view, table));

            DxfDocument doc = DxfDocument.Load(path);
            Text title = doc.Entities.All.OfType<Text>().Single(t => t.Value == "전기 사양표");
            Text body = doc.Entities.All.OfType<Text>().Single(t => t.Value == "A-1");

            // 1200mm / 200mm = 6배. 제목 5.0*6, 본문 3.2*6.
            Assert.Equal(30.0, title.Height, 6);
            Assert.Equal(19.2, body.Height, 6);

            var specLines = doc.Entities.All.OfType<Line>()
                .Where(l => l.Layer.Name == DxfExporter.ElectricalSpecLayer).ToList();
            double minSpecX = specLines.Min(l => Math.Min(l.StartPoint.X, l.EndPoint.X));
            double maxSpecX = specLines.Max(l => Math.Max(l.StartPoint.X, l.EndPoint.X));
            Assert.Equal(0, minSpecX, 6);
            Assert.Equal(780, maxSpecX, 6);

            double titleTop = title.Position.Y + title.Height / 2;
            Assert.True(titleTop < -20, "표 제목이 치수선 아래에 있어야 합니다.");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ExportPlan_CapsSpecTableScaleForVeryLargeMold()
    {
        var table = ElectricalSpecTableBuilder.Build(
            new[] { new ElectricalCircuitInput("A-1", 20000) },
            HeatingWireSpec.Case1, new PowerSupplySpec());
        var view = new DxfView();
        view.VisibleEdges.Add(new DxfPolyline
        {
            Points = new[] { new Vec2(0, 0), new Vec2(4000, 0), new Vec2(4000, 2000), new Vec2(0, 2000) }
        });
        view.HeatLines.Add(new DxfPolyline
        {
            Points = new[] { new Vec2(100, 100), new Vec2(3900, 100) }
        });

        string path = Path.Combine(Path.GetTempPath(),
            "hm_plan_scale_cap_" + Guid.NewGuid().ToString("N") + ".dxf");
        try
        {
            Assert.True(DxfExporter.ExportPlan(path, view, table));

            DxfDocument doc = DxfDocument.Load(path);
            Text title = doc.Entities.All.OfType<Text>().Single(t => t.Value == "전기 사양표");
            Text body = doc.Entities.All.OfType<Text>().Single(t => t.Value == "A-1");

            // 4000mm / 200mm = 20배지만 상한 16배.
            Assert.Equal(80.0, title.Height, 6);
            Assert.Equal(51.2, body.Height, 6);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ExportPlan_KeepsMinimumSpecTableSizeForSmallMold()
    {
        var table = ElectricalSpecTableBuilder.Build(
            new[] { new ElectricalCircuitInput("A-1", 20000) },
            HeatingWireSpec.Case1, new PowerSupplySpec());
        var view = new DxfView();
        view.VisibleEdges.Add(new DxfPolyline
        {
            Points = new[] { new Vec2(0, 0), new Vec2(200, 0), new Vec2(200, 100), new Vec2(0, 100) }
        });
        view.HeatLines.Add(new DxfPolyline
        {
            Points = new[] { new Vec2(10, 10), new Vec2(190, 10) }
        });

        string path = Path.Combine(Path.GetTempPath(),
            "hm_plan_scale_min_" + Guid.NewGuid().ToString("N") + ".dxf");
        try
        {
            Assert.True(DxfExporter.ExportPlan(path, view, table));

            DxfDocument doc = DxfDocument.Load(path);
            Text title = doc.Entities.All.OfType<Text>().Single(t => t.Value == "전기 사양표");
            Text body = doc.Entities.All.OfType<Text>().Single(t => t.Value == "A-1");

            Assert.Equal(5.0, title.Height, 6);
            Assert.Equal(3.2, body.Height, 6);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void FromHlr_SubtractsConfirmedPlaneOrigin()
    {
        var frame = new ProjectionFrame(
            new Vec3(100, 200, 300), new Vec3(0, 0, 1), new Vec3(1, 0, 0));

        Vec2 local = PlanViewBuilder.FromHlr(new Vec2(150, -50), frame);

        Assert.Equal(50, local.X, 9);
        Assert.Equal(-250, local.Y, 9);
    }

    [Fact]
    public void FromHlr_MatchesProjectionForTiltedFrame()
    {
        var frame = new ProjectionFrame(
            new Vec3(10, -20, 30), new Vec3(1, 2, 2), new Vec3(2, -1, 0));
        var point = new Vec3(13, 7, -4);
        var hlr = new Vec2(
            Vec3.Dot(point, frame.AxisX), Vec3.Dot(point, frame.AxisY));

        Vec2 local = PlanViewBuilder.FromHlr(hlr, frame);
        Vec2 expected = frame.Project(point);

        Assert.Equal(expected.X, local.X, 9);
        Assert.Equal(expected.Y, local.Y, 9);
    }

    [Fact]
    public void TryProjectRoute_RejectsFullyDegenerateRoute()
    {
        var frame = new ProjectionFrame(
            new Vec3(0, 0, 0), new Vec3(0, 0, 1), new Vec3(1, 0, 0));

        bool ok = PlanViewBuilder.TryProjectRoute(
            new[] { new Vec3(5, 5, 0), new Vec3(5, 5, 10) }, frame,
            out _, out string failure);

        Assert.False(ok);
        Assert.Contains("법선", failure);
    }

    [Fact]
    public void TryProjectRoute_CollapsesOnlyCoincidentProjectedPoints()
    {
        var frame = new ProjectionFrame(
            new Vec3(0, 0, 0), new Vec3(0, 0, 1), new Vec3(1, 0, 0));

        bool ok = PlanViewBuilder.TryProjectRoute(
            new[] { new Vec3(0, 0, 0), new Vec3(10, 0, 5), new Vec3(10, 0, 20) }, frame,
            out IReadOnlyList<Vec2> projected, out string failure);

        Assert.True(ok, failure);
        Assert.Equal(2, projected.Count);
        Assert.Equal(new Vec2(0, 0), projected[0]);
        Assert.Equal(new Vec2(10, 0), projected[1]);
    }
}
