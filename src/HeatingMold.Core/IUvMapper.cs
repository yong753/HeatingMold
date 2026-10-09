namespace HeatingMold.Core;

/// <summary>3D 점 (mm).</summary>
public readonly record struct Point3D(double X, double Y, double Z);

/// <summary>UV → 3D 매핑 추상화.
/// 실제 구현(S3)은 브리지 SampleUvTo3D 사용, 테스트는 가짜 매퍼 사용.</summary>
public interface IUvMapper
{
    Point3D Map(double u, double v);
}
