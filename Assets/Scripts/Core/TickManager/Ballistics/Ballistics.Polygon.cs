using UnityEngine;

/// <summary>
/// 판의 실물 모양. **서브셀 격자 위에 얹히는 값이지 격자를 바꾸는 것이 아니다.**
///
/// 판 하나가 직사각형이 아니라 다각형일 수 있게 하는 최소한의 기하. 하는 일은 하나뿐이다 -
/// 다각형이 축 정렬 사각형(서브셀 하나)에 얼마나 잠기는지를 **정확한 넓이로** 돌려준다.
/// 샘플링이 아니라 클리핑이라 서브셀 해상도와 무관하게 이 값 자체는 정확하다.
///
/// 다만 정확한 것은 **넓이**지 경로가 아니다. 빗변이 서브셀 하나를 반으로 가르면 그 칸은
/// 0.5인데, 꽉 찬 쪽으로 지나간 탄도 빈 쪽으로 지나간 탄도 똑같이 0.5를 읽는다. 계단이
/// 그라데이션이 된 것이지 오차가 사라진 것이 아니다 - 자세한 것은 CLAUDE.md 불변식에.
/// </summary>
public static partial class Ballistics
{
    // Sutherland-Hodgman은 볼록 창으로 자를 때마다 꼭짓점이 최대 하나 늘어난다. 창이
    // 사각형이라 네 번 자르므로 원본 + 4가 상한이다. 넉넉히 잡아 두 배로 둔다.
    private const int MaxPolygonPoints = 64;

    private static readonly Vector2[] _clipA = new Vector2[MaxPolygonPoints];
    private static readonly Vector2[] _clipB = new Vector2[MaxPolygonPoints];
    private static readonly Vector2[] _hull = new Vector2[MaxPolygonPoints];

    // 모노톤 체인은 아래 껍질과 위 껍질을 이어 쓰는 동안 출력이 최대 2*count까지
    // 부푼다. 호출자의 dst에 바로 쓰면 dst가 입력과 같은 배열일 때(그게 보통이다)
    // 반드시 넘친다 - 여기로 쓰고 마지막에 잘라서 복사한다.
    private static readonly Vector2[] _hullOut = new Vector2[MaxPolygonPoints * 2];

    /// <summary>
    /// 점이 다각형 안인가. 광선 교차 세기.
    ///
    /// **넓이 클리핑과 따로 있는 이유는 해상도다.** 서브셀은 6x6이라 잠긴 비율로 재지만,
    /// 그림은 픽셀 단위라 예/아니오면 충분하고 그편이 훨씬 또렷하다. 그래서 눈에 보이는
    /// 실루엣은 서브셀 격자보다 촘촘하다 - 모양은 픽셀, 저항은 6x6이다.
    /// </summary>
    public static bool PolygonContains(Vector2[] poly, Vector2 point)
    {
        if (poly == null || poly.Length < 3)
            return true;

        bool inside = false;

        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
        {
            Vector2 a = poly[j];
            Vector2 b = poly[i];

            // 변이 점의 높이를 걸치는가. 한쪽만 >= 로 잡아야 꼭짓점을 두 번 안 센다.
            if (a.y > point.y != b.y > point.y
                && point.x < (b.x - a.x) * (point.y - a.y) / (b.y - a.y) + a.x)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    /// <summary>
    /// 점들의 볼록 껍질. 결과를 dst에 채우고 개수를 돌려준다.
    ///
    /// **두 띠가 만나는 칸을 위해 있다.** 한 칸에 판이 하나뿐이라 두 조각의 합집합을
    /// 그대로 담을 수 없는데, 꺾이는 자리에서는 합집합이 이미 볼록에 가까워서 껍질이
    /// 거의 같은 도형이다. 곧게 이어지는 자리에서는 아예 똑같다.
    ///
    /// 오목한 이음매에서는 껍질이 안쪽을 조금 더 채운다. 그것이 이 근사의 대가이고,
    /// 틈이 남는 것보다 낫다 - 틈은 우주가 보이지만 조금 더 찬 것은 안 보인다.
    /// </summary>
    public static int ConvexHull(Vector2[] points, int count, Vector2[] dst)
    {
        if (count < 3)
        {
            for (int i = 0; i < count; i++)
                dst[i] = points[i];

            return count;
        }

        // x, 같으면 y로 정렬. 삽입 정렬이면 충분하다 - 여기 오는 점이 스무 개 남짓이다.
        for (int i = 0; i < count; i++)
            _hull[i] = points[i];

        for (int i = 1; i < count; i++)
        {
            Vector2 key = _hull[i];
            int j = i - 1;

            while (j >= 0 && (_hull[j].x > key.x || (_hull[j].x == key.x && _hull[j].y > key.y)))
            {
                _hull[j + 1] = _hull[j];
                j--;
            }

            _hull[j + 1] = key;
        }

        int k = 0;

        // 아래쪽 그리고 위쪽. 마지막 점은 다음 사슬의 첫 점이라 하나씩 뺀다.
        for (int i = 0; i < count; i++)
        {
            while (k >= 2 && Cross(_hullOut[k - 2], _hullOut[k - 1], _hull[i]) <= 0f)
                k--;

            _hullOut[k++] = _hull[i];
        }

        for (int i = count - 2, lower = k + 1; i >= 0; i--)
        {
            while (k >= lower && Cross(_hullOut[k - 2], _hullOut[k - 1], _hull[i]) <= 0f)
                k--;

            _hullOut[k++] = _hull[i];
        }

        int n = Mathf.Min(Mathf.Max(0, k - 1), dst.Length);

        for (int i = 0; i < n; i++)
            dst[i] = _hullOut[i];

        return n;
    }

    private static float Cross(Vector2 o, Vector2 a, Vector2 b)
        => (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);

    /// <summary>다각형의 넓이. 감기 방향과 무관하다.</summary>
    public static float PolygonArea(Vector2[] poly)
    {
        if (poly == null || poly.Length < 3)
            return 0f;

        return PolygonArea(poly, poly.Length);
    }

    private static float PolygonArea(Vector2[] poly, int count)
    {
        if (count < 3)
            return 0f;

        float twice = 0f;

        for (int i = 0, j = count - 1; i < count; j = i++)
            twice += poly[j].x * poly[i].y - poly[i].x * poly[j].y;

        return Mathf.Abs(twice) * 0.5f;
    }

    /// <summary>
    /// 다각형과 축 정렬 사각형이 겹치는 넓이.
    ///
    /// **넓이만 돌려준다.** 잘린 모양을 들고 있을 이유가 없다 - 이 값을 쓰는 자리가
    /// "이 서브셀이 얼마나 실물인가" 하나뿐이고, 그건 스칼라다. 모양까지 보관하면
    /// 판마다 다각형이 36개 생기고 그걸 최신으로 유지할 책임이 따라온다.
    /// </summary>
    public static float ClippedArea(Vector2[] poly, Vector2 min, Vector2 max)
        => PolygonArea(_clipA, ClipToRect(poly, min, max, _clipA));

    /// <summary>
    /// 잘린 모양 자체를 dst에 채우고 꼭짓점 수를 돌려준다.
    ///
    /// 넓이만 필요한 자리(<see cref="ClippedArea"/>)와 모양이 필요한 자리(직선을 칸마다
    /// 조각내는 저작 도구)가 **같은 클리핑을 써야 한다** - 둘이 갈리면 그림에 보이는
    /// 모양과 탄이 만나는 저항이 서로 다른 도형이 된다.
    /// </summary>
    public static int ClipToRect(Vector2[] poly, Vector2 min, Vector2 max, Vector2[] dst)
    {
        if (poly == null || poly.Length < 3)
            return 0;

        int count = Mathf.Min(poly.Length, MaxPolygonPoints);

        for (int i = 0; i < count; i++)
            _clipA[i] = poly[i];

        count = ClipHalfPlane(_clipA, count, _clipB, 0, min.x, true);
        count = ClipHalfPlane(_clipB, count, _clipA, 0, max.x, false);
        count = ClipHalfPlane(_clipA, count, _clipB, 1, min.y, true);
        count = ClipHalfPlane(_clipB, count, _clipA, 1, max.y, false);

        if (!ReferenceEquals(dst, _clipA))
        {
            for (int i = 0; i < count; i++)
                dst[i] = _clipA[i];
        }

        return count;
    }

    /// <summary>
    /// 반평면 하나로 자른다. axis 0이면 x, 1이면 y. keepAbove면 limit 이상을 남긴다.
    ///
    /// 볼록한 창으로 자르는 것뿐이라 Sutherland-Hodgman이면 충분하다. 원본이 오목해도
    /// 결과 넓이는 맞는다 - 오목한 다각형에서 이 알고리즘이 만드는 것은 겹친 변이고,
    /// 신발끈 공식은 그 겹침을 부호로 상쇄한다.
    /// </summary>
    private static int ClipHalfPlane(Vector2[] src, int count, Vector2[] dst, int axis, float limit, bool keepAbove)
    {
        if (count == 0)
            return 0;

        int outCount = 0;

        for (int i = 0, j = count - 1; i < count; j = i++)
        {
            Vector2 a = src[j];
            Vector2 b = src[i];

            float av = axis == 0 ? a.x : a.y;
            float bv = axis == 0 ? b.x : b.y;

            bool aIn = keepAbove ? av >= limit : av <= limit;
            bool bIn = keepAbove ? bv >= limit : bv <= limit;

            if (aIn != bIn)
            {
                // 변이 경계를 가로지른다. 교점을 넣는다. 분모는 aIn != bIn이 보장하므로
                // 0이 될 수 없다 - 두 값이 limit의 서로 다른 쪽에 있다는 뜻이다.
                float t = (limit - av) / (bv - av);

                if (outCount < MaxPolygonPoints)
                    dst[outCount++] = a + (b - a) * t;
            }

            if (bIn && outCount < MaxPolygonPoints)
                dst[outCount++] = b;
        }

        return outCount;
    }
}
