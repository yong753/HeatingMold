#nullable enable annotations

using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using HeatingMold.App.Services;
using HeatingMold.Core.Circuits;
using HeatingMold.Core.Electrical;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Validation;
using HeatingMold.Application.Sessions;
using HeatingMold.Application.Transactions;
using HeatingMold.Application.UseCases;

namespace HeatingMold.App.ViewModels;

/// <summary>회로별 전기 입력, 초안 명령과 적용 상태를 관리합니다.</summary>
public sealed class ElectricalSpecificationViewModel : INotifyPropertyChanged
{
    private readonly ProjectSession _session;
    private readonly UpdateElectricalSpecification _updateElectricalSpecification;
    private readonly WireSpecificationInputsViewModel _wireSpecification;
    private DesignProject? _editorProject;
    private bool _editorInitialized;
    private string _statusText = "전기 입력 변경은 초안을 만든 뒤 적용하세요.";

    /// <summary>회로별로 편집할 실제 전압과 nullable 한도 행입니다.</summary>
    public ObservableCollection<ElectricalSupplyEditRow> CircuitSupplies { get; } = [];

    /// <summary>마지막 입력 검증·초안·적용 결과를 화면에 표시합니다.</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>현재 입력을 미적용 전기 초안으로 저장합니다.</summary>
    public ICommand CreateDraftCommand { get; }

    /// <summary>현재 전기 초안을 기존 곡선 측정값으로 적용합니다.</summary>
    public ICommand ApplyDraftCommand { get; }

    /// <summary>전기 사양 초안만 취소합니다.</summary>
    public ICommand CancelDraftCommand { get; }

    /// <summary>화면 입력 속성이 바뀌었음을 WPF 바인딩에 알립니다.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>초안 변경 뒤 계산 결과 패널에 최신 세션 snapshot을 전달합니다.</summary>
    public event Action<SessionSnapshot>? StateChanged;

    /// <summary>프로젝트 세션과 전기 사양 유스케이스를 화면 명령에 연결합니다.</summary>
    public ElectricalSpecificationViewModel(
        ProjectSession session,
        UpdateElectricalSpecification updateElectricalSpecification,
        WireSpecificationInputsViewModel wireSpecification)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _updateElectricalSpecification = updateElectricalSpecification
            ?? throw new ArgumentNullException(nameof(updateElectricalSpecification));
        _wireSpecification = wireSpecification
            ?? throw new ArgumentNullException(nameof(wireSpecification));

        CreateDraftCommand = new RelayCommand(CreateDraft);
        ApplyDraftCommand = new RelayCommand(ApplyDraft);
        CancelDraftCommand = new RelayCommand(CancelDraft);
        RefreshFromProject(session.Current.CurrentProject, force: true);
    }

    /// <summary>확정 프로젝트가 바뀌었을 때만 편집 입력과 회로 행을 다시 만듭니다.</summary>
    public void RefreshFromProject(DesignProject? project, bool force = false)
    {
        if (!force && _editorInitialized && ReferenceEquals(_editorProject, project))
            return;

        _editorInitialized = true;
        _editorProject = project;
        CircuitSupplies.Clear();

        if (project?.WireSpecification is null)
        {
            _wireSpecification.RefreshFromProject(project);
            return;
        }

        _wireSpecification.RefreshFromProject(project);
        foreach (Circuit circuit in project.Circuits.OrderBy(circuit => circuit.Sequence))
            CircuitSupplies.Add(new ElectricalSupplyEditRow(circuit));
    }

    private void CreateDraft()
    {
        DesignProject? project = _session.Current.CurrentProject;
        if (project?.WireSpecification is not { } currentWire)
        {
            Finish("확정된 발열선 사양이 있어야 전기 입력을 수정할 수 있습니다.");
            return;
        }
        if (string.IsNullOrWhiteSpace(_wireSpecification.MaterialText) ||
            !TryParsePositive(_wireSpecification.DiameterText, out double diameterMm) ||
            !TryParsePositive(_wireSpecification.ResistanceText, out double resistance))
        {
            Finish("재료명, 선경과 단위 길이당 저항을 유효한 양수로 입력하세요.");
            return;
        }
        if (diameterMm != currentWire.DiameterMm.Value)
        {
            Finish("선경 변경은 형상에 영향을 줍니다. 경로 생성 입력에서 다시 생성하세요.");
            return;
        }

        var supplies = new System.Collections.Generic.Dictionary<CircuitId, Supply>(CircuitSupplies.Count);
        foreach (ElectricalSupplyEditRow row in CircuitSupplies)
        {
            if (!row.TryCreateSupply(out Supply supply))
            {
                Finish($"{row.CircuitLabel}의 전압·전류·전력 한도는 비우거나 양수로 입력하세요.");
                return;
            }

            supplies.Add(row.CircuitId, supply);
        }

        WireSpecification replacement;
        try
        {
            replacement = new WireSpecification(
                _wireSpecification.MaterialText,
                new LengthMm(diameterMm),
                new ResistanceOhmsPerMeter(resistance));
        }
        catch (ArgumentException exception)
        {
            Finish(string.Concat("발열선 사양이 유효하지 않습니다: ", exception.Message));
            return;
        }

        OperationResult<InputDraft> result = _updateElectricalSpecification.CreateDraft(replacement, supplies);
        if (!result.IsSuccess)
        {
            Finish(string.Concat(
                "전기 입력 초안을 만들지 못했습니다: ",
                UserMessageFormatter.FormatIssues(result.Issues, "입력을 확인하세요.")));
            return;
        }

        Finish("전기 입력 초안을 만들었습니다. 적용 전까지 확정 설계와 계산 결과는 그대로입니다.");
    }

    private void ApplyDraft()
    {
        CandidateOutcome outcome = _updateElectricalSpecification.Apply();
        if (!outcome.IsCommitted)
        {
            Finish(string.Concat(
                "전기 사양을 적용하지 못했습니다: ",
                UserMessageFormatter.FormatIssues(outcome.Issues, "입력 초안과 설계 상태를 확인하세요.")));
            return;
        }

        SessionSnapshot snapshot = outcome.Snapshot!;
        StatusText = outcome.Status == CandidateStatus.PostCommitFailure
            ? string.Concat(
                "전기 사양은 확정했지만 화면 복구가 필요합니다: ",
                UserMessageFormatter.FormatIssues(outcome.Issues, "화면을 다시 구성하세요."))
            : "전기 사양과 회로별 계산 결과를 확정했습니다. Native 형상 측정값은 재사용했습니다.";
        RefreshFromProject(snapshot.CurrentProject);
        StateChanged?.Invoke(snapshot);
    }

    private void CancelDraft()
    {
        if (!_updateElectricalSpecification.CancelDraft())
        {
            Finish("취소할 전기 사양 초안이 없습니다.");
            return;
        }

        RefreshFromProject(_session.Current.CurrentProject, force: true);
        Finish("전기 사양 초안을 취소했습니다.");
    }

    private void Finish(string status)
    {
        StatusText = status;
        StateChanged?.Invoke(_session.Current);
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    private static bool TryParsePositive(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) &&
        double.IsFinite(value) && value > 0;

}

/// <summary>하나의 회로 전원 입력을 문자열로 편집하고 nullable 값으로 변환합니다.</summary>
public sealed class ElectricalSupplyEditRow
{
    /// <summary>전원 입력이 속한 안정 회로 ID입니다.</summary>
    public CircuitId CircuitId { get; }

    /// <summary>화면에 표시할 회로 순번입니다.</summary>
    public string CircuitLabel { get; }

    /// <summary>실제 전압 입력이며 비우면 미지정입니다.</summary>
    public string ActualVoltage { get; set; }

    /// <summary>최대 전압 입력이며 비우면 한도가 없습니다.</summary>
    public string MaximumVoltage { get; set; }

    /// <summary>최대 전류 입력이며 비우면 한도가 없습니다.</summary>
    public string MaximumCurrent { get; set; }

    /// <summary>최대 전력 입력이며 비우면 한도가 없습니다.</summary>
    public string MaximumPower { get; set; }

    /// <summary>확정 회로의 전원 한도를 현재 문화권 문자열로 표시합니다.</summary>
    public ElectricalSupplyEditRow(Circuit circuit)
    {
        ArgumentNullException.ThrowIfNull(circuit);
        CircuitId = circuit.Id;
        CircuitLabel = string.Format(CultureInfo.CurrentCulture, "회로 {0}", circuit.Sequence + 1);
        ActualVoltage = FormatOptional(circuit.Supply.ActualVoltageVrms?.Value);
        MaximumVoltage = FormatOptional(circuit.Supply.MaximumVoltageVrms?.Value);
        MaximumCurrent = FormatOptional(circuit.Supply.MaximumCurrentAmps?.Value);
        MaximumPower = FormatOptional(circuit.Supply.MaximumPowerWatts?.Value);
    }

    /// <summary>비어 있는 값과 양의 유한 수치를 구분해 Supply로 변환합니다.</summary>
    public bool TryCreateSupply(out Supply supply)
    {
        supply = null;
        if (!TryParseOptionalPositive(ActualVoltage, out double? actualVoltage) ||
            !TryParseOptionalPositive(MaximumVoltage, out double? maximumVoltage) ||
            !TryParseOptionalPositive(MaximumCurrent, out double? maximumCurrent) ||
            !TryParseOptionalPositive(MaximumPower, out double? maximumPower))
        {
            return false;
        }

        supply = new Supply(
            actualVoltage is { } actual ? new VoltageVrms(actual) : null,
            maximumVoltage is { } voltageLimit ? new VoltageVrms(voltageLimit) : null,
            maximumCurrent is { } currentLimit ? new CurrentAmps(currentLimit) : null,
            maximumPower is { } powerLimit ? new PowerWatts(powerLimit) : null);
        return true;
    }

    private static bool TryParseOptionalPositive(string text, out double? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text))
            return true;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double parsed) ||
            !double.IsFinite(parsed) || parsed <= 0)
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static string FormatOptional(double? value) =>
        value?.ToString("R", CultureInfo.CurrentCulture) ?? string.Empty;
}
