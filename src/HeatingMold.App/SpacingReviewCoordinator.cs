using System;

namespace HeatingMold.App;

/// <summary>
/// 간격 분석 요청의 세대와 게시된 결과를 관리합니다. 늦게 끝난 이전 요청의 결과가 새 결과를
/// 덮지 않도록 게시 시점에 세대·지문을 실제로 비교하고, 같은 지문의 직전 결과는 재사용합니다.
/// WPF·네이티브에 의존하지 않아 단위 테스트할 수 있습니다.
/// </summary>
internal sealed class SpacingReviewCoordinator
{
    private long _generation;
    private string _requestedFingerprint = string.Empty;
    private string _publishedFingerprint = string.Empty;
    private string _publishedSummary = string.Empty;
    private SpacingMarker[] _publishedMarkers = Array.Empty<SpacingMarker>();
    private bool _hasPublished;

    internal long Generation => _generation;

    internal string RequestedFingerprint => _requestedFingerprint;

    /// <summary>새 요청을 시작하고 세대를 올립니다. 이전 요청의 결과는 이후 게시되지 않습니다.</summary>
    internal long BeginRequest(string? fingerprint)
    {
        _generation++;
        _requestedFingerprint = fingerprint ?? string.Empty;
        return _generation;
    }

    /// <summary>이미 게시된 결과가 현재 요청과 같은 지문인지 확인합니다(캐시 재사용).</summary>
    internal bool TryGetPublished(string? fingerprint, out SpacingMarker[] markers, out string summary)
    {
        if (_hasPublished && string.Equals(_publishedFingerprint, fingerprint, StringComparison.Ordinal))
        {
            markers = _publishedMarkers;
            summary = _publishedSummary;
            return true;
        }

        markers = Array.Empty<SpacingMarker>();
        summary = string.Empty;
        return false;
    }

    /// <summary>게시 직전에 이 결과가 아직 현재 요청의 것인지 확인합니다.</summary>
    internal bool IsCurrent(long generation, string? fingerprint)
        => generation == _generation &&
           string.Equals(_requestedFingerprint, fingerprint, StringComparison.Ordinal);

    internal bool TryPublish(long generation, string? fingerprint, SpacingMarker[] markers, string? summary)
    {
        if (!IsCurrent(generation, fingerprint)) return false;

        _publishedFingerprint = fingerprint ?? string.Empty;
        _publishedMarkers = markers ?? Array.Empty<SpacingMarker>();
        _publishedSummary = summary ?? string.Empty;
        _hasPublished = true;
        return true;
    }

    /// <summary>모든 대기 상태와 캐시를 버립니다. 세대를 올려 진행 중인 분석 결과를 무효화합니다.</summary>
    internal void Invalidate()
    {
        _generation++;
        _requestedFingerprint = string.Empty;
        _publishedFingerprint = string.Empty;
        _publishedMarkers = Array.Empty<SpacingMarker>();
        _publishedSummary = string.Empty;
        _hasPublished = false;
    }
}
