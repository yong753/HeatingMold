using HeatingMold.Core.Electrical;
using HeatingMold.Core.Geometry;
using HeatingMold.Export;
using netDxf;
using netDxf.Entities;

namespace HeatingMold.Core.Tests;

public class DxfExporterTests
{
    [Fact]
    public void ExportPlan_WritesSelectableSpecTable()
    {
        var table = ElectricalSpecTableBuilder.Build(
            new[] { new ElectricalCircuitInput("A-1", 20000) },
            HeatingWireSpec.Case1, new PowerSupplySpec());
        var view = new DxfView();
        view.VisibleEdges.Add(new DxfPolyline
        {
            Points = new[] { new Vec2(0, 0), new Vec2(1000, 0), new Vec2(1000, 500), new Vec2(0, 500) }
        });

        string path = Path.Combine(Path.GetTempPath(),
            "hm_spec_" + Guid.NewGuid().ToString("N") + ".dxf");
        try
        {
            Assert.True(DxfExporter.ExportPlan(path, view, table));

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

    [Fact]
    public void ExportPlan_WritesClosedHeatAreaOnDedicatedLayer()
    {
        var view = new DxfView();
        view.VisibleEdges.Add(new DxfPolyline
        {
            Points = new[]
            {
                new Vec2(0, 0), new Vec2(1200, 0), new Vec2(1200, 600), new Vec2(0, 600)
            },
            Closed = true
        });
        view.HeatAreas.Add(new DxfPolyline
        {
            Points = new[]
            {
                new Vec2(10, 20), new Vec2(1190, 20), new Vec2(1190, 580), new Vec2(10, 580)
            },
            Closed = true
        });

        string path = Path.Combine(Path.GetTempPath(),
            "hm_heat_area_" + Guid.NewGuid().ToString("N") + ".dxf");
        try
        {
            Assert.True(DxfExporter.ExportPlan(path, view));

            DxfDocument doc = DxfDocument.Load(path);
            Assert.True(doc.Layers.Contains(DxfExporter.HeatAreaLayer));

            Polyline2D area = Assert.Single(doc.Entities.All.OfType<Polyline2D>()
                .Where(p => p.Layer.Name == DxfExporter.HeatAreaLayer));
            Assert.True(area.IsClosed);
            Assert.Equal(4, area.Vertexes.Count);
            Assert.Equal(10, area.Vertexes[0].Position.X, 6);
            Assert.Equal(20, area.Vertexes[0].Position.Y, 6);
            Assert.Equal(1190, area.Vertexes[2].Position.X, 6);
            Assert.Equal(580, area.Vertexes[2].Position.Y, 6);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ExportPlan_WritesEachHeatAreaWithItsOwnColor()
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
            },
            Closed = true
        });
        view.HeatAreas.Add(new DxfPolyline
        {
            Points = new[]
            {
                new Vec2(10, 20), new Vec2(590, 20), new Vec2(590, 580), new Vec2(10, 580)
            },
            Closed = true,
            Color = new AciColor(255, 0, 0)
        });
        view.HeatAreas.Add(new DxfPolyline
        {
            Points = new[]
            {
                new Vec2(610, 20), new Vec2(1190, 20), new Vec2(1190, 580), new Vec2(610, 580)
            },
            Closed = true,
            Color = new AciColor(0, 0, 255)
        });

        string path = Path.Combine(Path.GetTempPath(),
            "hm_heat_area_color_" + Guid.NewGuid().ToString("N") + ".dxf");
        try
        {
            Assert.True(DxfExporter.ExportPlan(path, view, table));

            DxfDocument doc = DxfDocument.Load(path);
            List<Polyline2D> areas = doc.Entities.All.OfType<Polyline2D>()
                .Where(p => p.Layer.Name == DxfExporter.HeatAreaLayer).ToList();
            Assert.Equal(2, areas.Count);

            Polyline2D red = areas.Single(a =>
                a.Color.R == 255 && a.Color.G == 0 && a.Color.B == 0);
            Polyline2D blue = areas.Single(a =>
                a.Color.R == 0 && a.Color.G == 0 && a.Color.B == 255);
            Assert.Equal(10, red.Vertexes[0].Position.X, 6);
            Assert.Equal(610, blue.Vertexes[0].Position.X, 6);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ExportPlan_PlacesVerticalDimensionOnLeftWithReadableText()
    {
        var view = new DxfView();
        view.VisibleEdges.Add(new DxfPolyline
        {
            Points = new[]
            {
                new Vec2(0, 0), new Vec2(1200, 0), new Vec2(1200, 600), new Vec2(0, 600)
            },
            Closed = true
        });

        string path = Path.Combine(Path.GetTempPath(),
            "hm_plan_dim_" + Guid.NewGuid().ToString("N") + ".dxf");
        try
        {
            Assert.True(DxfExporter.ExportPlan(path, view));

            DxfDocument doc = DxfDocument.Load(path);
            List<AlignedDimension> dims = doc.Entities.All.OfType<AlignedDimension>().ToList();
            Assert.Equal(2, dims.Count);

            AlignedDimension vertical = dims.Single(d => Math.Abs(d.Measurement - 600) < 1e-6);
            AlignedDimension horizontal = dims.Single(d => Math.Abs(d.Measurement - 1200) < 1e-6);

            Assert.All(dims, d => Assert.Equal(20.0, d.Style.TextHeight, 6));

            // 세로 치수선은 금형 왼쪽(minX - 20)에 있고 보조선은 오른쪽으로 뻗는다.
            Assert.Equal(-20.0, vertical.DimLinePosition.X, 6);
            // 기존 가로 치수선은 금형 아래에 유지된다.
            Assert.Equal(-20.0, horizontal.DimLinePosition.Y, 6);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ExportPlan_WritesEachHeatRouteWithItsOwnColor()
    {
        var view = new DxfView();

        view.VisibleEdges.Add(new DxfPolyline
        {
            Points = new[]
            {
                new Vec2(0, 0), new Vec2(1200, 0), new Vec2(1200, 600), new Vec2(0, 600)
            },
            Closed = true
        });
        view.HeatLines.Add(new DxfPolyline
        {
            Points = new[] { new Vec2(10, 20), new Vec2(590, 20) },
            Color = new AciColor(255, 0, 0)
        });
        view.HeatLines.Add(new DxfPolyline
        {
            Points = new[] { new Vec2(610, 20), new Vec2(1190, 20) },
            Color = new AciColor(0, 0, 255)
        });

        string path = Path.Combine(Path.GetTempPath(),
            "hm_heat_line_color_" + Guid.NewGuid().ToString("N") + ".dxf");
        try
        {
            Assert.True(DxfExporter.ExportPlan(path, view));

            DxfDocument doc = DxfDocument.Load(path);
            List<Polyline2D> lines = doc.Entities.All.OfType<Polyline2D>()
                .Where(p => p.Layer.Name == DxfExporter.HeatWireLayer).ToList();
            Assert.Equal(2, lines.Count);
            Assert.Contains(lines, l => l.Color.R == 255 && l.Color.G == 0 && l.Color.B == 0);
            Assert.Contains(lines, l => l.Color.R == 0 && l.Color.G == 0 && l.Color.B == 255);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ExportPlan_WritesRouteLengthLabelsOnDedicatedLayer()
    {
        var view = new DxfView();
        view.VisibleEdges.Add(new DxfPolyline
        {
            Points = new[]
            {
                new Vec2(0, 0), new Vec2(1200, 0), new Vec2(1200, 600), new Vec2(0, 600)
            },
            Closed = true
        });
        view.HeatLines.Add(new DxfPolyline
        {
            Points = new[] { new Vec2(100, 300), new Vec2(1100, 300) },
            Color = new AciColor(255, 0, 0)
        });
        view.HeatLabels.Add(new DxfRouteLabel
        {
            Text = "1000.0",
            Position = new Vec2(600, 306),
            RotationDeg = 0,
            Color = new AciColor(255, 0, 0)
        });
        view.HeatLabels.Add(new DxfRouteLabel
        {
            Text = "50.0",
            Position = new Vec2(1130, 330),
            RotationDeg = 90,
            HasLeader = true,
            LeaderStart = new Vec2(1100, 300),
            LeaderEnd = new Vec2(1130, 326)
        });
        view.HeatLabelLeaders.Add(new DxfPolyline
        {
            Points = new[] { new Vec2(1100, 300), new Vec2(1130, 326) }
        });

        string path = Path.Combine(Path.GetTempPath(),
            "hm_length_label_" + Guid.NewGuid().ToString("N") + ".dxf");
        try
        {
            Assert.True(DxfExporter.ExportPlan(path, view, lengthNote: RouteLengthLabels.UnitNote));

            DxfDocument doc = DxfDocument.Load(path);
            Assert.True(doc.Layers.Contains(DxfExporter.HeatLengthLabelLayer));

            List<Text> labels = doc.Entities.All.OfType<Text>()
                .Where(t => t.Layer.Name == DxfExporter.HeatLengthLabelLayer).ToList();

            Text lane = labels.Single(t => t.Value == "1000.0");
            Assert.Equal(DxfExporter.RouteLengthLabelHeight, lane.Height, 6);
            Assert.Equal(TextAlignment.MiddleCenter, lane.Alignment);
            Assert.Equal(600, lane.Position.X, 6);
            Assert.Equal(306, lane.Position.Y, 6);
            Assert.Equal(255, lane.Color.R);
            Assert.Equal(0, lane.Color.G);

            Text turn = labels.Single(t => t.Value == "50.0");
            Assert.Equal(90, turn.Rotation, 6);

            // 단위는 라벨마다 반복하지 않고 도면에 한 번만 적는다.
            Assert.Single(labels.Where(t => t.Value == RouteLengthLabels.UnitNote));

            Line leader = Assert.Single(doc.Entities.All.OfType<Line>()
                .Where(l => l.Layer.Name == DxfExporter.HeatLengthLabelLayer));
            Assert.Equal(1100, leader.StartPoint.X, 6);
            Assert.Equal(326, leader.EndPoint.Y, 6);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ExportPlan_KeepsDimensionsAndTableIndependentOfLabels()
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
            },
            Closed = true
        });
        view.HeatLines.Add(new DxfPolyline
        {
            Points = new[] { new Vec2(100, 100), new Vec2(1100, 100) }
        });
        // 도면 밖으로 나가는 라벨도 금형 치수와 전기표 위치를 바꾸면 안 된다.
        view.HeatLabels.Add(new DxfRouteLabel
        {
            Text = "1000.0",
            Position = new Vec2(600, 5000),
            RotationDeg = 0
        });

        string path = Path.Combine(Path.GetTempPath(),
            "hm_length_label_bounds_" + Guid.NewGuid().ToString("N") + ".dxf");
        try
        {
            Assert.True(DxfExporter.ExportPlan(path, view, table));

            DxfDocument doc = DxfDocument.Load(path);
            List<AlignedDimension> dims = doc.Entities.All.OfType<AlignedDimension>().ToList();
            Assert.Equal(2, dims.Count);
            Assert.Contains(dims, d => Math.Abs(d.Measurement - 1200) < 1e-6);
            Assert.Contains(dims, d => Math.Abs(d.Measurement - 600) < 1e-6);
            Assert.All(dims, d => Assert.Equal(20.0, d.Style.TextHeight, 6));

            Text title = doc.Entities.All.OfType<Text>().Single(t => t.Value == "전기 사양표");
            Assert.True(title.Position.Y < -20, "표 제목이 치수선 아래에 있어야 합니다.");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
