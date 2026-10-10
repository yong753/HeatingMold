namespace HeatingMold.Core.Geometry;

/// <summary>
/// UV 파라미터 격자를 투영 2D로 사영해 둔 역매핑 테이블.
/// 투영 2D 점 → 격자 삼각형 위치 + 무게중심 보간으로 (u,v) 를 되돌린다.
/// 격자 밖(경계에 아주 근접한 점)은 최근접 삼각형에서 무게중심을 외삽한다
/// (평면/완만 곡면에서는 아핀이라 외삽이 정확).
/// 전개 없이 "투영 2D → 면(UV)" 를 근사하기 위한 것.
/// </summary>
public sealed class UvProjectionGrid
{
    private readonly int _n;
    private readonly double[] _u;   // (n+1)^2, idx = i*(n+1)+j  (i: u축, j: v축)
    private readonly double[] _v;
    private readonly double[] _x;   // 투영 2D
    private readonly double[] _y;

    public UvProjectionGrid(int n, double[] u, double[] v, double[] x, double[] y)
    {
        if (n < 1) throw new ArgumentOutOfRangeException(nameof(n), "n >= 1 필요");
        int count = (n + 1) * (n + 1);
        if (u.Length != count || v.Length != count || x.Length != count || y.Length != count)
            throw new ArgumentException("격자 배열 길이가 (n+1)^2 와 불일치");
        _n = n;
        _u = u; _v = v; _x = x; _y = y;
    }

    private int Idx(int i, int j) => i * (_n + 1) + j;

    /// <summary>투영 2D 점 → (u,v). 내부면 삼각형 보간, 근소하게 밖이면 최근접 삼각형 외삽.</summary>
    public bool TryMap(Vec2 p, out double u, out double v)
    {
        // 1차: 내부 삼각형
        for (int i = 0; i < _n; i++)
            for (int j = 0; j < _n; j++)
            {
                int a = Idx(i, j), b = Idx(i + 1, j), c = Idx(i, j + 1), d = Idx(i + 1, j + 1);
                if (!Finite(a) || !Finite(b) || !Finite(c) || !Finite(d)) continue;

                if (Bary(p, a, b, c, out double l0, out double l1, out double l2) && l0 >= -1e-9 && l1 >= -1e-9 && l2 >= -1e-9)
                { u = InterpU(a, b, c, l0, l1, l2); v = InterpV(a, b, c, l0, l1, l2); return true; }
                if (Bary(p, b, d, c, out l0, out l1, out l2) && l0 >= -1e-9 && l1 >= -1e-9 && l2 >= -1e-9)
                { u = InterpU(b, d, c, l0, l1, l2); v = InterpV(b, d, c, l0, l1, l2); return true; }
            }

        // 2차: 최근접(최소 위반) 삼각형에서 외삽
        int ba = -1, bb = -1, bc = -1;
        double bl0 = 0, bl1 = 0, bl2 = 0, bestMin = double.NegativeInfinity;
        for (int i = 0; i < _n; i++)
            for (int j = 0; j < _n; j++)
            {
                int a = Idx(i, j), b = Idx(i + 1, j), c = Idx(i, j + 1), d = Idx(i + 1, j + 1);
                if (!Finite(a) || !Finite(b) || !Finite(c) || !Finite(d)) continue;

                Consider(p, a, b, c, ref ba, ref bb, ref bc, ref bl0, ref bl1, ref bl2, ref bestMin);
                Consider(p, b, d, c, ref ba, ref bb, ref bc, ref bl0, ref bl1, ref bl2, ref bestMin);
            }

        // 근소한 외삽(셀 절반 이내)만 허용 — 멀리 벗어나면 폭주 방지를 위해 최근접 폴백
        if (ba >= 0 && bestMin >= -0.5)
        {
            u = InterpU(ba, bb, bc, bl0, bl1, bl2);
            v = InterpV(ba, bb, bc, bl0, bl1, bl2);
            return true;
        }

        return Nearest(p, out u, out v);
    }

    private void Consider(Vec2 p, int ia, int ib, int ic,
        ref int ba, ref int bb, ref int bc, ref double bl0, ref double bl1, ref double bl2, ref double bestMin)
    {
        if (!Bary(p, ia, ib, ic, out double l0, out double l1, out double l2)) return;
        double m = Math.Min(l0, Math.Min(l1, l2));
        if (m > bestMin) { bestMin = m; ba = ia; bb = ib; bc = ic; bl0 = l0; bl1 = l1; bl2 = l2; }
    }

    private double InterpU(int a, int b, int c, double l0, double l1, double l2)
        => l0 * _u[a] + l1 * _u[b] + l2 * _u[c];

    private double InterpV(int a, int b, int c, double l0, double l1, double l2)
        => l0 * _v[a] + l1 * _v[b] + l2 * _v[c];

    private bool Finite(int k)
    {
        double x = _x[k], y = _y[k];
        return !double.IsNaN(x) && !double.IsNaN(y);
    }

    private bool Bary(Vec2 p, int ia, int ib, int ic, out double l0, out double l1, out double l2)
    {
        double ax = _x[ia], ay = _y[ia], bx = _x[ib], by = _y[ib], cx = _x[ic], cy = _y[ic];
        double den = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy);
        if (Math.Abs(den) < 1e-12) { l0 = l1 = l2 = 0; return false; }
        l0 = ((by - cy) * (p.X - cx) + (cx - bx) * (p.Y - cy)) / den;
        l1 = ((cy - ay) * (p.X - cx) + (ax - cx) * (p.Y - cy)) / den;
        l2 = 1 - l0 - l1;
        return true;
    }

    private bool Nearest(Vec2 p, out double u, out double v)
    {
        u = v = double.NaN;
        double best = double.MaxValue;
        bool found = false;
        for (int k = 0; k < _x.Length; k++)
        {
            if (!Finite(k)) continue;
            double dx = _x[k] - p.X, dy = _y[k] - p.Y;
            double d = dx * dx + dy * dy;
            if (d < best) { best = d; u = _u[k]; v = _v[k]; found = true; }
        }
        return found;
    }
}
