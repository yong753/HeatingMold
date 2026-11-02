using HeatingMold.Core.Electrical;
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
}
