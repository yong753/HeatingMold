using System;
using HeatingMold.App.Persistence;
using HeatingMold.Core.Geometry;

namespace HeatingMold.App
{
    /// <summary>
    /// 기본 예제 모델별 로드 프리셋. 파일명이 아니라 내용 해시로 판별하므로 같은 파일을
    /// '예제 열기'와 'STEP 열기' 어느 쪽으로 열어도 동일한 작업 좌표를 사용한다.
    /// </summary>
    internal static class ExampleModelPresets
    {
        // resource\Mold_Cover_Trim.step — 열린 셸 1개 / 80면 / 958,841.060 mm².
        // 사용자 확정: 가열면 +X.
        public const string MoldCoverTrimSha256 =
            "8CB03405817606D1917FEF55B46A60011FFB5E93075EECDEF5A8651D4359FCED";

        public static bool TryGetWorkingNormal(string path, out Vec3 normal)
        {
            normal = default;
            if (string.IsNullOrEmpty(path)) return false;

            string sha = ProjectJsonStore.TryComputeFileSha256(path);
            if (!string.Equals(sha, MoldCoverTrimSha256, StringComparison.OrdinalIgnoreCase)) return false;

            // +X 법선. 기존 평면 축 규칙이 X축 (0,1,0) → Y축 (0,0,1) 을 선택하므로
            // 화면 기준 오른쪽 +Y, 위 +Z 가 유지된다.
            normal = new Vec3(1, 0, 0);
            return true;
        }
    }
}
