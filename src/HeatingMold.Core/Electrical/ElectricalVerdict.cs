namespace HeatingMold.Core.Electrical;

public enum ElectricalVerdict
{
    Ok,
    InvalidInput,
    OverCurrent,
    OverPower,
    OverCurrentAndPower
}

public static class ElectricalVerdictText
{
    public static string ToText(ElectricalVerdict verdict) => verdict switch
    {
        ElectricalVerdict.Ok => "OK",
        ElectricalVerdict.OverCurrent => "전류 초과",
        ElectricalVerdict.OverPower => "전력 초과",
        ElectricalVerdict.OverCurrentAndPower => "전류·전력 초과",
        _ => "입력 오류"
    };

    public static string ToShortText(ElectricalVerdict verdict) => verdict switch
    {
        ElectricalVerdict.Ok => "OK",
        ElectricalVerdict.OverCurrent => "전류",
        ElectricalVerdict.OverPower => "전력",
        ElectricalVerdict.OverCurrentAndPower => "전류·전력",
        _ => "오류"
    };
}
