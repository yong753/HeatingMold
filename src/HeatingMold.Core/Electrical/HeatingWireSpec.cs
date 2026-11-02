namespace HeatingMold.Core.Electrical;

/// <summary>발열사 제원. r은 상온 기준 단위 길이당 저항(Ω/m)이다.</summary>
public sealed record HeatingWireSpec
{
    public string Material { get; init; } = "SUS 316L";
    public double DiameterMm { get; init; }
    public double ResistanceOhmPerMeter { get; init; }

    /// <summary>PDF Case 1 — SUS 316L, Ø0.3 mm, 4 Ω/m.</summary>
    public static HeatingWireSpec Case1 { get; } = new()
    {
        DiameterMm = 0.30,
        ResistanceOhmPerMeter = 4.0
    };

    /// <summary>PDF Case 2 — SUS 316L, Ø0.15 mm, 8 Ω/m.</summary>
    public static HeatingWireSpec Case2 { get; } = new()
    {
        DiameterMm = 0.15,
        ResistanceOhmPerMeter = 8.0
    };

    public string Describe() =>
        FormattableString.Invariant($"{Material} Ø{DiameterMm:0.###}mm {ResistanceOhmPerMeter:0.###}Ω/m");
}
