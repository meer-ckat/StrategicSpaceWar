using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 모듈이 어디에 설 수 있는가. 격자가 아니라 **기하**로 판정한다 - 이 배는 격자가 판에만
/// 있고 모듈은 실수 좌표 위에 떠 있다(1.9 m 포, 45도 발사대, 경사판, 폴리곤 판). 정수
/// 발자국으로 접었다가 하루 만에 되돌렸다(2026-09-05) - 반 칸 스냅, 1.9 m는 몇 칸이냐,
/// 45도 발사대 16칸이 전부 실수 기하를 정수에 억지로 접는 비용이었다.
///
/// 모듈의 자리는 **상자 중심 칸** 하나다 - 판정·수명(KillRear)·조각 이동·마운트가 전부
/// 이 칸을 본다. 원점(localPosition)과 중심이 offset으로 갈라지면 죽는 칸이 어긋난다.
///
/// 판과의 관계는 관통 깊이가 아니라 **판 넓이 중 덮인 비율**로 잰다 - 깊이 하나로는
/// 0.4 m 패널의 62%를 먹고도 통과하고, 경사판은 깊이 축이 애매하다. 페인터와 빌더가
/// 같은 함수를 쓴다.
/// </summary>
public static class ModulePlacement
{
    public enum Verdict { Ok, InSpace, InPlate, OnModule, NoMount, Buried }

    public struct Plate
    {
        public Vector2Int cell;
        public Vector2[] poly;
        public float area;
    }

    public struct Other
    {
        public Vector2Int origin;
        public Vector2[] poly;
    }

    public struct Result
    {
        public Verdict verdict;
        public Vector2Int cell;      // 상자 중심 칸 = 이 모듈의 자리
        public bool hasMount;
        public Vector2Int mount;     // 덮인 넓이가 제일 큰 판. 죽으면 같이 죽는 판
        public string detail;

        /// <summary>Buried는 경고지 거부가 아니다 - 배 안에 통째로 묻힌 포는 선체 직속으로 산다.</summary>
        public bool Allowed => verdict == Verdict.Ok || verdict == Verdict.Buried;
    }

    /// <summary>배 좌표(칸 단위): x = col, y = -row.</summary>
    public static Vector2 CellToShip(Vector2Int cell) => new(cell.x, -cell.y);

    public static Vector2Int ShipToCell(Vector2 p) => new(Mathf.RoundToInt(p.x), Mathf.RoundToInt(-p.y));

    /// <summary>모듈 상자의 네 귀퉁이(배 좌표). def 콜라이더 + 배치 size/offset + rot.</summary>
    public static Vector2[] ModulePolygon(
        Vector2Int origin, string def, float rot, Vector2 size, Vector2 offset, out Vector2 centre, out Vector2 box)
    {
        ShipBuilder.ModuleBox(def, rot, size, offset, out box, out Vector2 shipOffset);
        centre = CellToShip(origin) + shipOffset;
        return Corners(centre, box, rot);
    }

    /// <summary>
    /// 판 폴리곤(배 좌표). shape는 콜라이더 중심 기준 칸 로컬이고 회전 전이다 -
    /// Armor.FootprintLocal과 같은 규칙이라 그림·후면·이 판정이 같은 도형을 본다.
    /// </summary>
    public static Vector2[] PlatePolygon(
        Vector2Int cell, string def, float rot, Vector2 size, Vector2 offset, Vector2[] shape)
    {
        ShipBuilder.ModuleBox(def, rot, size, offset, out Vector2 box, out Vector2 shipOffset);
        Vector2 centre = CellToShip(cell) + shipOffset;

        if (shape == null || shape.Length < 3)
            return Corners(centre, box, rot);

        var pts = new Vector2[shape.Length];

        for (int i = 0; i < shape.Length; i++)
            pts[i] = centre + Ballistics.Rotate(shape[i], rot);

        return pts;
    }

    public static Result Evaluate(
        string def, Vector2Int origin, float rot, Vector2 size, Vector2 offset,
        Func<Vector2Int, bool> onShip, IReadOnlyList<Plate> plates, IReadOnlyList<Other> others)
    {
        Vector2[] box = ModulePolygon(origin, def, rot, size, offset, out Vector2 centre, out Vector2 boxSize);
        float halfDiag = boxSize.magnitude * 0.5f;
        bool exterior = ShipBuilder.IsExteriorModule(def);

        var r = new Result { verdict = Verdict.Ok, cell = ShipToCell(centre) };

        // 1. 다른 모듈. 겹친 두 상자는 한 탄에 둘 다 맞는다 - 사실상 금지.
        for (int i = 0; i < others.Count; i++)
        {
            Other o = others[i];

            if (o.origin == origin)
                continue;

            float a = OverlapArea(box, o.poly);

            if (a > Ballistics.ModuleOverlapMax)
            {
                r.verdict = Verdict.OnModule;
                r.detail = $"({o.origin.x},{o.origin.y})의 모듈과 {a:0.00} m² 겹침";
                return r;
            }
        }

        // 2. 판. 실내 모듈은 판을 30% 넘게 덮으면 파묻힌 것. 외장은 덮어도 된다(포대는 선체에 박혀 있다).
        float best = 0f;
        Vector2Int bestCell = default;
        float near = (halfDiag + 2f) * (halfDiag + 2f);

        for (int i = 0; i < plates.Count; i++)
        {
            Plate p = plates[i];

            if ((CellToShip(p.cell) - centre).sqrMagnitude > near)
                continue;

            float a = OverlapArea(box, p.poly);

            if (a <= 1e-4f)
                continue;

            if (!exterior && p.area > 0f && a / p.area > Ballistics.ModulePlateCoverMax)
            {
                r.verdict = Verdict.InPlate;
                r.detail = $"판 ({p.cell.x},{p.cell.y})의 {a / p.area:P0}를 덮음";
                return r;
            }

            if (a > best)
            {
                best = a;
                bestCell = p.cell;
            }
        }

        // 3. 우주. 상자가 중심을 덮는 칸이 하나라도 우주면 실내 모듈은 못 선다.
        bool anySpace = false;
        int reach = Mathf.CeilToInt(halfDiag) + 1;

        for (int dc = -reach; dc <= reach && !anySpace; dc++)
        for (int dr = -reach; dr <= reach; dr++)
        {
            var c = new Vector2Int(r.cell.x + dc, r.cell.y + dr);

            if (Ballistics.PolygonContains(box, CellToShip(c)) && !onShip(c))
            {
                anySpace = true;
                break;
            }
        }

        if (!exterior && anySpace)
        {
            r.verdict = Verdict.InSpace;
            r.detail = "우주 칸을 덮음 - 실내 모듈은 실내에만";
            return r;
        }

        // 4. 마운트 = 덮인 넓이가 제일 큰 판. 안 닿았으면 외장은 reach만큼 키운 상자로 한 번 더.
        if (best > 0f)
        {
            r.hasMount = true;
            r.mount = bestCell;
            return r;
        }

        if (!exterior)
            return r;   // 실내 모듈은 판 없이 후면 위에 선다

        Vector2[] wide = Corners(centre, boxSize + Vector2.one * (2f * Ballistics.ModuleMountReach), rot);

        for (int i = 0; i < plates.Count; i++)
        {
            Plate p = plates[i];

            if ((CellToShip(p.cell) - centre).sqrMagnitude > near)
                continue;

            float a = OverlapArea(wide, p.poly);

            if (a > best)
            {
                best = a;
                bestCell = p.cell;
            }
        }

        if (best > 0f)
        {
            r.hasMount = true;
            r.mount = bestCell;
            return r;
        }

        if (anySpace)
        {
            r.verdict = Verdict.NoMount;
            r.detail = $"배에 안 붙어 있다 - 판과 겹치거나 {Ballistics.ModuleMountReach} m 안이어야 한다";
            return r;
        }

        r.verdict = Verdict.Buried;
        r.detail = "상자가 판 밖으로 안 나온다 - 관통 전엔 안 맞는다";
        return r;
    }

    public static string Reason(Verdict v) => v switch
    {
        Verdict.InSpace => "우주다.",
        Verdict.InPlate => "판에 파묻혔다.",
        Verdict.OnModule => "다른 모듈과 겹친다.",
        Verdict.NoMount => "배에 안 붙어 있다.",
        Verdict.Buried => "배 안에 묻혔다(경고).",
        _ => "",
    };

    /// <summary>
    /// 가로축 대칭 복사본의 배치 offset. rot은 -rot, 배치 offset.y는 부호가 바뀌는데, def
    /// 콜라이더 offset은 def 안에 있어 못 뒤집는다 - 그 y 성분이 0이 아니면 복사본의 상자가
    /// 반대로 밀린다(rot 90 포: 원본 -0.85, 복사본 +0.85). 그 차이를 배치 offset이 메운다:
    /// o' = (o.x - 2·dy·sin, -o.y - 2·dy·cos). dy = 0이면 예전 그대로 (o.x, -o.y).
    /// </summary>
    public static Vector2 MirrorOffset(Vector2 defOffset, float rot, Vector2 offset)
    {
        float s = Mathf.Sin(rot * Mathf.Deg2Rad), c = Mathf.Cos(rot * Mathf.Deg2Rad);
        return new Vector2(offset.x - 2f * defOffset.y * s, -offset.y - 2f * defOffset.y * c);
    }

    /// <summary>선체 직속 모듈의 자리 칸. 콜라이더 offset을 더한 상자 중심이다 - 원점이 아니다.</summary>
    public static Vector2Int CentreCellOf(Transform module, ShipGrid.Map map)
    {
        Vector2 local = module.localPosition;

        if (module.TryGetComponent(out BoxCollider2D box))
            local += Ballistics.Rotate(box.offset, module.localEulerAngles.z);

        return map.ToCell(local);
    }

    // ---------------------------------------------------------------
    // 기하

    public static Vector2[] Corners(Vector2 centre, Vector2 size, float rot)
    {
        float hx = Mathf.Abs(size.x) * 0.5f, hy = Mathf.Abs(size.y) * 0.5f;

        return new[]
        {
            centre + Ballistics.Rotate(new Vector2(-hx, -hy), rot),
            centre + Ballistics.Rotate(new Vector2(hx, -hy), rot),
            centre + Ballistics.Rotate(new Vector2(hx, hy), rot),
            centre + Ballistics.Rotate(new Vector2(-hx, hy), rot),
        };
    }

    private static List<Vector2> _a = new(), _b = new();   // 클리핑 버퍼 둘, 반평면마다 자리를 바꾼다

    /// <summary>
    /// 볼록 다각형(clip)으로 subject를 잘라 넓이를 준다. Sutherland-Hodgman. subject가
    /// 오목해도 넓이는 맞는다 - Ballistics.ClipToRect와 같은 이유(겹친 변을 신발끈이 상쇄).
    /// clip은 모듈 상자라 언제나 볼록이다.
    /// </summary>
    public static float OverlapArea(Vector2[] clip, Vector2[] subject)
    {
        if (clip == null || subject == null || clip.Length < 3 || subject.Length < 3)
            return 0f;

        // 감기 방향을 반시계로 맞춘다. 반평면 판정이 그 방향을 전제한다.
        float sign = Mathf.Sign(SignedArea(clip));

        _a.Clear();
        _a.AddRange(subject);

        for (int i = 0; i < clip.Length && _a.Count > 0; i++)
        {
            Vector2 p = clip[i], q = clip[(i + 1) % clip.Length];
            Vector2 edge = (q - p) * sign;

            _b.Clear();

            for (int j = 0; j < _a.Count; j++)
            {
                Vector2 cur = _a[j], prev = _a[(j + _a.Count - 1) % _a.Count];
                float dc = Cross(edge, cur - p), dp = Cross(edge, prev - p);

                if (dc >= 0f)
                {
                    if (dp < 0f)
                        _b.Add(Intersect(prev, cur, dp, dc));

                    _b.Add(cur);
                }
                else if (dp >= 0f)
                    _b.Add(Intersect(prev, cur, dp, dc));
            }

            (_a, _b) = (_b, _a);
        }

        if (_a.Count < 3)
            return 0f;

        float area = 0f;

        for (int i = 0; i < _a.Count; i++)
        {
            Vector2 u = _a[i], v = _a[(i + 1) % _a.Count];
            area += u.x * v.y - v.x * u.y;
        }

        return Mathf.Abs(area) * 0.5f;
    }

    private static Vector2 Intersect(Vector2 prev, Vector2 cur, float dp, float dc)
        => prev + (cur - prev) * (dp / (dp - dc));

    private static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

    private static float SignedArea(Vector2[] poly)
    {
        float area = 0f;

        for (int i = 0; i < poly.Length; i++)
        {
            Vector2 u = poly[i], v = poly[(i + 1) % poly.Length];
            area += u.x * v.y - v.x * u.y;
        }

        return area * 0.5f;
    }
}
