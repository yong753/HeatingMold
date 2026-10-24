using System;
using System.Windows;
using HeatingMold.Application.Ports.Export;
using HeatingMold.Core.Validation;
using Microsoft.Win32;

namespace HeatingMold.App;

public partial class MainWindow
{
    /// <summary>확정 설계 snapshot을 DXF로 저장할 위치를 선택합니다.</summary>
    private void ExportDesignDxf_Click(object sender, RoutedEventArgs e)
    {
        SelectDesignExportPath(ExportFormat.Dxf);
    }

    /// <summary>확정 설계 snapshot을 STEP으로 저장할 위치를 선택합니다.</summary>
    private void ExportDesignStep_Click(object sender, RoutedEventArgs e)
    {
        SelectDesignExportPath(ExportFormat.Step);
    }

    /// <summary>형식에 맞는 파일 대화상자를 열고 사용자가 선택한 설정으로 export를 실행합니다.</summary>
    private void SelectDesignExportPath(ExportFormat format)
    {
        string extension = format == ExportFormat.Dxf ? ".dxf" : ".step";
        string formatName = GetExportFormatName(format);
        var dialog = new SaveFileDialog
        {
            Title = $"{formatName} 저장",
            Filter = $"{formatName} 파일 (*{extension})|*{extension}",
            DefaultExt = extension,
            AddExtension = true,
            FileName = $"HeatingMold-design{extension}",
            OverwritePrompt = true
        };

        if (dialog.ShowDialog(this) != true)
            return;

        bool includeSourceModel = format == ExportFormat.Step &&
            IncludeSourceModelInStepBox.IsChecked == true;
        ExportDesignToPath(dialog.FileName, format, includeSourceModel);
    }

    /// <summary>Application export 유스케이스를 실행하고 파일 기록 결과를 상태 표시줄에 알립니다.</summary>
    private void ExportDesignToPath(string path, ExportFormat format, bool includeSourceModel)
    {
        try
        {
            var options = new ExportOptions(format, includeSourceModel);
            OperationResult<ExportReceipt> result = _exportDesign.Execute(path, options);
            if (result.IsSuccess)
            {
                ExportReceipt receipt = result.GetValueOrThrow();
                StatusText.Text = $"{GetExportFormatName(format)} 저장 완료: {receipt.Path}";
                return;
            }

            StatusText.Text = $"{GetExportFormatName(format)} 저장 실패: {FormatIssues(result.Issues)}";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"{GetExportFormatName(format)} 저장 실패: {exception.Message}";
        }
    }

    /// <summary>export 형식을 사용자가 이해하기 쉬운 이름으로 바꿉니다.</summary>
    private static string GetExportFormatName(ExportFormat format) => format switch
    {
        ExportFormat.Dxf => "설계 DXF",
        ExportFormat.Step => "설계 STEP",
        _ => "설계 파일"
    };
}
