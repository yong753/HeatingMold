namespace HeatingMold.Core.Model;

/// <summary>선택된 면 정보 (S3 픽킹/콤보에서 생성, 사행 생성 입력).</summary>
public sealed record FacePlacement(int FaceIndex, string? DisplayName = null);
