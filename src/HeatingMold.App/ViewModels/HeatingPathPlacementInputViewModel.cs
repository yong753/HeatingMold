#nullable enable annotations

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace HeatingMold.App.ViewModels;

/// <summary>경로 배치 입력과 계산 중심 피치 표시를 관리합니다.</summary>
public sealed class HeatingPathPlacementInputViewModel : INotifyPropertyChanged
{
    private string _boundaryMarginText = string.Empty;
    private string _minimumClearGapText = string.Empty;
    private string _insertionDepthText = string.Empty;
    private readonly WireSpecificationInputsViewModel _wireSpecification;

    /// <summary>공유 발열선 입력의 지름 변경을 중심 피치 표시와 연결합니다.</summary>
    public HeatingPathPlacementInputViewModel(WireSpecificationInputsViewModel wireSpecification)
    {
        _wireSpecification = wireSpecification
            ?? throw new ArgumentNullException(nameof(wireSpecification));
        _wireSpecification.PropertyChanged += OnWireSpecificationPropertyChanged;
    }

    /// <summary>영역 경계에서 확보할 여유를 현재 문화권 문자열로 보관합니다.</summary>
    public string BoundaryMarginText
    {
        get => _boundaryMarginText;
        set => SetProperty(ref _boundaryMarginText, value ?? string.Empty);
    }

    /// <summary>발열사 바깥면 사이에 확보할 최소 간격을 보관합니다.</summary>
    public string MinimumClearGapText
    {
        get => _minimumClearGapText;
        set
        {
            if (SetProperty(ref _minimumClearGapText, value ?? string.Empty))
                OnPropertyChanged(nameof(CenterPitchText));
        }
    }

    /// <summary>금형 표면에서 발열사 바깥면까지의 삽입 깊이를 보관합니다.</summary>
    public string InsertionDepthText
    {
        get => _insertionDepthText;
        set => SetProperty(ref _insertionDepthText, value ?? string.Empty);
    }

    /// <summary>최소 간격과 도체 지름의 합을 화면에 표시합니다.</summary>
    public string CenterPitchText
    {
        get
        {
            if (!TryParsePositive(MinimumClearGapText, out double minimumClearGapMm) ||
                !TryParsePositive(_wireSpecification.DiameterText, out double wireDiameterMm) ||
                !TryCalculateCenterPitch(minimumClearGapMm, wireDiameterMm, out double centerPitchMm))
            {
                return "—";
            }

            return centerPitchMm.ToString("F2", CultureInfo.CurrentCulture);
        }
    }

    /// <summary>화면 입력이 바뀌었음을 WPF 바인딩에 알립니다.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>문자열 입력을 검증해 생성 유스케이스가 사용할 수치 묶음으로 변환합니다.</summary>
    public bool TryCreateParameters(out HeatingPathPlacementParameters? parameters)
    {
        parameters = null;
        if (!TryParseFinite(BoundaryMarginText, out double boundaryMarginMm) || boundaryMarginMm < 0 ||
            !TryParsePositive(MinimumClearGapText, out double minimumClearGapMm) ||
            !TryParseFinite(InsertionDepthText, out double insertionDepthMm) || insertionDepthMm < 0 ||
            !TryParsePositive(_wireSpecification.DiameterText, out double wireDiameterMm) ||
            !TryCalculateCenterPitch(minimumClearGapMm, wireDiameterMm, out double centerPitchMm))
        {
            return false;
        }

        parameters = new HeatingPathPlacementParameters(
            boundaryMarginMm,
            minimumClearGapMm,
            insertionDepthMm,
            wireDiameterMm,
            centerPitchMm);
        return true;
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private void OnWireSpecificationPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(WireSpecificationInputsViewModel.DiameterText))
            OnPropertyChanged(nameof(CenterPitchText));
    }

    private static bool TryParseFinite(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) &&
        double.IsFinite(value);

    private static bool TryParsePositive(string text, out double value) =>
        TryParseFinite(text, out value) && value > 0;

    private static bool TryCalculateCenterPitch(
        double minimumClearGapMm,
        double wireDiameterMm,
        out double centerPitchMm)
    {
        centerPitchMm = minimumClearGapMm + wireDiameterMm;
        if (double.IsFinite(centerPitchMm))
            return true;

        centerPitchMm = 0;
        return false;
    }
}

/// <summary>검증된 경계 여유, 최소 간격, 선경과 중심 피치입니다.</summary>
public sealed record HeatingPathPlacementParameters
{
    /// <summary>영역 경계에서 확보할 여유(mm)입니다.</summary>
    public double BoundaryMarginMm { get; }

    /// <summary>발열사 바깥면 사이의 최소 간격(mm)입니다.</summary>
    public double MinimumClearGapMm { get; }

    /// <summary>발열사 바깥면까지의 삽입 깊이(mm)입니다.</summary>
    public double InsertionDepthMm { get; }

    /// <summary>발열 도체의 실제 지름(mm)입니다.</summary>
    public double WireDiameterMm { get; }

    /// <summary>인접 경로 중심 사이의 계산 피치(mm)입니다.</summary>
    public double CenterPitchMm { get; }

    /// <summary>이미 검증된 경로 배치 입력값을 보관합니다.</summary>
    internal HeatingPathPlacementParameters(
        double boundaryMarginMm,
        double minimumClearGapMm,
        double insertionDepthMm,
        double wireDiameterMm,
        double centerPitchMm)
    {
        BoundaryMarginMm = boundaryMarginMm;
        MinimumClearGapMm = minimumClearGapMm;
        InsertionDepthMm = insertionDepthMm;
        WireDiameterMm = wireDiameterMm;
        CenterPitchMm = centerPitchMm;
    }
}
