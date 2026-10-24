using HeatingMold.Core.Primitives;

namespace HeatingMold.Core.Electrical;

/// <summary>발열 도체 재료명과 전기·기하 제원을 보관합니다.</summary>
public sealed record WireSpecification
{
    /// <summary>도체를 표시하고 추적하는 재료명입니다.</summary>
    public string MaterialName { get; }

    /// <summary>도체 지름(mm)입니다.</summary>
    public LengthMm DiameterMm { get; }

    /// <summary>도체의 단위 길이당 저항(Ω/m)입니다.</summary>
    public ResistanceOhmsPerMeter ResistanceOhmsPerMeter { get; }

    /// <summary>재료명과 양의 유한 지름·단위 저항으로 도체 사양을 만듭니다.</summary>
    public WireSpecification(
        string materialName,
        LengthMm diameterMm,
        ResistanceOhmsPerMeter resistanceOhmsPerMeter)
    {
        if (string.IsNullOrWhiteSpace(materialName))
            throw new ArgumentException("도체 재료명을 입력해야 합니다.", nameof(materialName));
        if (diameterMm.Value <= 0)
            throw new ArgumentOutOfRangeException(nameof(diameterMm), "도체 지름은 양수여야 합니다.");
        if (resistanceOhmsPerMeter.Value <= 0)
            throw new ArgumentOutOfRangeException(nameof(resistanceOhmsPerMeter), "단위 저항은 양수여야 합니다.");

        MaterialName = materialName.Trim();
        DiameterMm = diameterMm;
        ResistanceOhmsPerMeter = resistanceOhmsPerMeter;
    }
}

/// <summary>회로별 실제 전압과 허용 한도를 보관합니다.</summary>
public sealed record Supply
{
    /// <summary>계산에 사용할 실제 전압(V RMS)입니다.</summary>
    public VoltageVrms? ActualVoltageVrms { get; }

    /// <summary>회로별 최대 허용 전압(V RMS)입니다.</summary>
    public VoltageVrms? MaximumVoltageVrms { get; }

    /// <summary>회로별 최대 허용 전류(A)입니다.</summary>
    public CurrentAmps? MaximumCurrentAmps { get; }

    /// <summary>선택 입력인 회로별 최대 허용 전력(W)입니다.</summary>
    public PowerWatts? MaximumPowerWatts { get; }

    /// <summary>실제값과 한도를 nullable로 보관해 누락 입력을 명시합니다.</summary>
    public Supply(
        VoltageVrms? actualVoltageVrms,
        VoltageVrms? maximumVoltageVrms,
        CurrentAmps? maximumCurrentAmps,
        PowerWatts? maximumPowerWatts = null)
    {
        ActualVoltageVrms = actualVoltageVrms;
        MaximumVoltageVrms = maximumVoltageVrms;
        MaximumCurrentAmps = maximumCurrentAmps;
        MaximumPowerWatts = maximumPowerWatts;
    }
}
