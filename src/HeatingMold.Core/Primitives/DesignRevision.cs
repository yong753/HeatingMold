namespace HeatingMold.Core.Primitives;

/// <summary>확정 설계의 성공한 변경 횟수를 나타내는 revision입니다.</summary>
public readonly record struct DesignRevision
{
    /// <summary>설계 revision 숫자입니다.</summary>
    public long Value { get; }

    /// <summary>숫자가 0 이상인 설계 revision을 만듭니다.</summary>
    public DesignRevision(long value)
    {
        if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), "설계 revision은 음수일 수 없습니다.");
        Value = value;
    }

    /// <summary>성공한 설계 변경을 반영한 다음 revision을 반환합니다.</summary>
    public DesignRevision Next() => new(checked(Value + 1));
}
