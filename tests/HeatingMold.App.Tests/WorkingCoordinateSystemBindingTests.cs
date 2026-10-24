using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Xunit;

namespace HeatingMold.App.Tests;

/// <summary>작업축 선택 UI가 ViewModel 양방향 바인딩을 사용하는지 검사합니다.</summary>
public sealed class WorkingCoordinateSystemBindingTests
{
    private static readonly HashSet<string> EventAttributeNames = new(StringComparer.Ordinal)
    {
        "CellEditEnding",
        "Checked",
        "Click",
        "SelectionChanged",
        "Unchecked"
    };

    /// <summary>축 콤보 상자가 선택 인덱스를 작업 좌표계 ViewModel과 양방향으로 연결합니다.</summary>
    [Fact]
    public void WorkingAxisComboBox_BindsSelectedIndexToViewModel()
    {
        string xamlPath = Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml");
        XDocument document = XDocument.Load(xamlPath);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement comboBox = Assert.Single(document.Descendants(presentation + "ComboBox"),
            element => (string)element.Attribute(xaml + "Name") == "WorkingAxisBox");

        Assert.Equal(
            "{Binding WorkingCoordinates.SelectedAxisIndex, Mode=TwoWay}",
            (string)comboBox.Attribute("SelectedIndex"));

        XElement statusText = Assert.Single(document.Descendants(presentation + "TextBlock"),
            element => (string)element.Attribute(xaml + "Name") == "WorkingCoordinateSystemText");
        Assert.Equal(
            "{Binding WorkingCoordinates.DisplayStatus}",
            (string)statusText.Attribute("Text"));
    }

    /// <summary>XAML에 선언한 화면 이벤트가 실제 MainWindow 처리기를 가리킵니다.</summary>
    [Fact]
    public void MainWindowXaml_EventHandlersResolveToMethods()
    {
        XDocument document = LoadMainWindowXaml();
        string[] handlerNames = document
            .Descendants()
            .Attributes()
            .Where(attribute => EventAttributeNames.Contains(attribute.Name.LocalName))
            .Select(attribute => attribute.Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(handlerNames);

        foreach (string handlerName in handlerNames)
        {
            MethodInfo handler = typeof(MainWindow).GetMethod(
                handlerName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.True(handler is not null, $"XAML 이벤트 처리기가 없습니다: {handlerName}");
        }
    }

    private static XDocument LoadMainWindowXaml() =>
        XDocument.Load(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml"));
}
