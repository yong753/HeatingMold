#nullable enable annotations

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using HeatingMold.Core.Electrical;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Primitives;

namespace HeatingMold.App.ViewModels;

/// <summary>경로 생성에 필요한 외부 좌표, 연결 여유와 전원 입력을 관리합니다.</summary>
internal sealed class HeatingPathCircuitInputViewModel : INotifyPropertyChanged
{
    private string _startExternalXText = string.Empty;
    private string _startExternalYText = string.Empty;
    private string _startExternalZText = string.Empty;
    private string _endExternalXText = string.Empty;
    private string _endExternalYText = string.Empty;
    private string _endExternalZText = string.Empty;
    private string _moldExitAllowanceText = string.Empty;
    private string _externalConnectionAllowanceText = string.Empty;
    private string _actualVoltageText = string.Empty;
    private string _maximumVoltageText = string.Empty;
    private string _maximumCurrentText = string.Empty;
    private string _maximumPowerText = string.Empty;

    /// <summary>시작부 외부 연결점 X 좌표 입력입니다.</summary>
    public string StartExternalXText
    {
        get => _startExternalXText;
        set => SetProperty(ref _startExternalXText, value ?? string.Empty);
    }

    /// <summary>시작부 외부 연결점 Y 좌표 입력입니다.</summary>
    public string StartExternalYText
    {
        get => _startExternalYText;
        set => SetProperty(ref _startExternalYText, value ?? string.Empty);
    }

    /// <summary>시작부 외부 연결점 Z 좌표 입력입니다.</summary>
    public string StartExternalZText
    {
        get => _startExternalZText;
        set => SetProperty(ref _startExternalZText, value ?? string.Empty);
    }

    /// <summary>끝부 외부 연결점 X 좌표 입력입니다.</summary>
    public string EndExternalXText
    {
        get => _endExternalXText;
        set => SetProperty(ref _endExternalXText, value ?? string.Empty);
    }

    /// <summary>끝부 외부 연결점 Y 좌표 입력입니다.</summary>
    public string EndExternalYText
    {
        get => _endExternalYText;
        set => SetProperty(ref _endExternalYText, value ?? string.Empty);
    }

    /// <summary>끝부 외부 연결점 Z 좌표 입력입니다.</summary>
    public string EndExternalZText
    {
        get => _endExternalZText;
        set => SetProperty(ref _endExternalZText, value ?? string.Empty);
    }

    /// <summary>금형 위 연장 구간에 더할 길이 입력입니다.</summary>
    public string MoldExitAllowanceText
    {
        get => _moldExitAllowanceText;
        set => SetProperty(ref _moldExitAllowanceText, value ?? string.Empty);
    }

    /// <summary>외부 연결선 구간에 더할 길이 입력입니다.</summary>
    public string ExternalConnectionAllowanceText
    {
        get => _externalConnectionAllowanceText;
        set => SetProperty(ref _externalConnectionAllowanceText, value ?? string.Empty);
    }

    /// <summary>회로 계산에 사용할 실제 전압 입력입니다.</summary>
    public string ActualVoltageText
    {
        get => _actualVoltageText;
        set => SetProperty(ref _actualVoltageText, value ?? string.Empty);
    }

    /// <summary>회로별 최대 전압 한도 입력입니다.</summary>
    public string MaximumVoltageText
    {
        get => _maximumVoltageText;
        set => SetProperty(ref _maximumVoltageText, value ?? string.Empty);
    }

    /// <summary>회로별 최대 전류 한도 입력입니다.</summary>
    public string MaximumCurrentText
    {
        get => _maximumCurrentText;
        set => SetProperty(ref _maximumCurrentText, value ?? string.Empty);
    }

    /// <summary>회로별 최대 전력 한도 입력입니다.</summary>
    public string MaximumPowerText
    {
        get => _maximumPowerText;
        set => SetProperty(ref _maximumPowerText, value ?? string.Empty);
    }

    /// <summary>입력 변경을 WPF 바인딩에 알립니다.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>문자열 입력을 유한한 좌표, 길이와 선택 전원 사양으로 변환합니다.</summary>
    public bool TryCreateInputs(out HeatingPathCircuitInputs? inputs, out string errorMessage)
    {
        inputs = null;
        errorMessage = string.Empty;

        if (!TryReadPoint(
                StartExternalXText,
                StartExternalYText,
                StartExternalZText,
                out Vec3 startExternalPoint))
        {
            errorMessage = "시작부 외부 연결 좌표에 유한한 mm 값 X, Y, Z를 입력하세요.";
            return false;
        }

        if (!TryReadPoint(
                EndExternalXText,
                EndExternalYText,
                EndExternalZText,
                out Vec3 endExternalPoint))
        {
            errorMessage = "끝부 외부 연결 좌표에 유한한 mm 값 X, Y, Z를 입력하세요.";
            return false;
        }

        if (!TryParseNonNegative(MoldExitAllowanceText, out double moldExitAllowanceMm))
        {
            errorMessage = "금형 위 연장 구간의 추가 여유를 0 이상의 mm 값으로 입력하세요.";
            return false;
        }

        if (!TryParseNonNegative(ExternalConnectionAllowanceText, out double externalAllowanceMm))
        {
            errorMessage = "외부 연결선 구간의 추가 여유를 0 이상의 mm 값으로 입력하세요.";
            return false;
        }

        if (!TryParseOptionalPositive(ActualVoltageText, out double? actualVoltage))
        {
            errorMessage = "실제 전압은 비우거나 0보다 큰 V RMS 값으로 입력하세요.";
            return false;
        }

        if (!TryParseOptionalPositive(MaximumVoltageText, out double? maximumVoltage))
        {
            errorMessage = "최대 전압은 비우거나 0보다 큰 V RMS 값으로 입력하세요.";
            return false;
        }

        if (!TryParseOptionalPositive(MaximumCurrentText, out double? maximumCurrent))
        {
            errorMessage = "최대 전류는 비우거나 0보다 큰 A 값으로 입력하세요.";
            return false;
        }

        if (!TryParseOptionalPositive(MaximumPowerText, out double? maximumPower))
        {
            errorMessage = "최대 전력은 비우거나 0보다 큰 W 값으로 입력하세요.";
            return false;
        }

        var supply = new Supply(
            actualVoltage is { } actual ? new VoltageVrms(actual) : null,
            maximumVoltage is { } voltageLimit ? new VoltageVrms(voltageLimit) : null,
            maximumCurrent is { } currentLimit ? new CurrentAmps(currentLimit) : null,
            maximumPower is { } powerLimit ? new PowerWatts(powerLimit) : null);

        inputs = new HeatingPathCircuitInputs(
            startExternalPoint,
            endExternalPoint,
            new LengthMm(moldExitAllowanceMm),
            new LengthMm(externalAllowanceMm),
            supply);
        return true;
    }

    private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static bool TryReadPoint(string xText, string yText, string zText, out Vec3 point)
    {
        point = default;
        if (!TryParseFinite(xText, out double x) ||
            !TryParseFinite(yText, out double y) ||
            !TryParseFinite(zText, out double z))
        {
            return false;
        }

        point = new Vec3(x, y, z);
        return true;
    }

    private static bool TryParseOptionalPositive(string text, out double? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text))
            return true;
        if (!TryParseFinite(text, out double parsed) || parsed <= 0)
            return false;

        value = parsed;
        return true;
    }

    private static bool TryParseNonNegative(string text, out double value) =>
        TryParseFinite(text, out value) && value >= 0;

    private static bool TryParseFinite(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) &&
        double.IsFinite(value);
}

/// <summary>검증한 경로 생성용 외부 좌표, 연결 여유와 전원 사양입니다.</summary>
internal sealed record HeatingPathCircuitInputs(
    Vec3 StartExternalPoint,
    Vec3 EndExternalPoint,
    LengthMm MoldExitAllowance,
    LengthMm ExternalConnectionAllowance,
    Supply Supply);
