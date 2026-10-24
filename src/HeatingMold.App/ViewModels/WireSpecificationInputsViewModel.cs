#nullable enable annotations

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using HeatingMold.Core.Electrical;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;

namespace HeatingMold.App.ViewModels;

/// <summary>경로 생성과 전기 편집에서 함께 사용하는 발열선 입력을 보관합니다.</summary>
public sealed class WireSpecificationInputsViewModel : INotifyPropertyChanged
{
    private string _materialText = string.Empty;
    private string _diameterText = string.Empty;
    private string _resistanceText = string.Empty;

    /// <summary>발열선 재료명 입력입니다.</summary>
    public string MaterialText
    {
        get => _materialText;
        set => SetProperty(ref _materialText, value ?? string.Empty);
    }

    /// <summary>발열 도체의 실제 지름 입력입니다.</summary>
    public string DiameterText
    {
        get => _diameterText;
        set => SetProperty(ref _diameterText, value ?? string.Empty);
    }

    /// <summary>발열선의 단위 길이당 저항 입력입니다.</summary>
    public string ResistanceText
    {
        get => _resistanceText;
        set => SetProperty(ref _resistanceText, value ?? string.Empty);
    }

    /// <summary>화면 입력 변경을 WPF 바인딩에 알립니다.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>재료명과 저항 입력을 검증해 경로 생성용 도체 사양으로 변환합니다.</summary>
    public bool TryCreateSpecification(
        double diameterMm,
        out WireSpecification? specification,
        out string errorMessage)
    {
        specification = null;
        errorMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(MaterialText))
        {
            errorMessage = "발열선 재료명을 입력하세요.";
            return false;
        }

        if (!double.TryParse(
                ResistanceText,
                NumberStyles.Float,
                CultureInfo.CurrentCulture,
                out double resistanceOhmsPerMeter) ||
            !double.IsFinite(resistanceOhmsPerMeter) ||
            resistanceOhmsPerMeter <= 0)
        {
            errorMessage = "단위 길이당 저항(Ω/m)을 0보다 큰 유한한 값으로 입력하세요.";
            return false;
        }

        try
        {
            specification = new WireSpecification(
                MaterialText,
                new LengthMm(diameterMm),
                new ResistanceOhmsPerMeter(resistanceOhmsPerMeter));
            return true;
        }
        catch (ArgumentException exception)
        {
            errorMessage = string.Concat("발열선 사양이 유효하지 않습니다: ", exception.Message);
            return false;
        }
    }

    /// <summary>현재 확정 프로젝트의 도체 사양을 편집 문자열로 불러옵니다.</summary>
    public void RefreshFromProject(DesignProject? project)
    {
        if (project?.WireSpecification is not { } wireSpecification)
        {
            MaterialText = string.Empty;
            DiameterText = string.Empty;
            ResistanceText = string.Empty;
            return;
        }

        MaterialText = wireSpecification.MaterialName;
        DiameterText = FormatNumber(wireSpecification.DiameterMm.Value);
        ResistanceText = FormatNumber(wireSpecification.ResistanceOhmsPerMeter.Value);
    }

    private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static string FormatNumber(double value) => value.ToString("R", CultureInfo.CurrentCulture);
}
