using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 탄도용 자체 충돌 세계 - 멀티스레딩의 1단계. Physics2D 레이캐스트가 메인 스레드
/// 족쇄라(2D엔 RaycastCommand가 없다), 판·모듈 콜라이더를 틱당 한 번 OBB 배열로
/// 떠서(스냅샷) 선분 교차를 순수 산술로 푼다. 배열과 수식뿐이라 나중에 그대로
/// NativeArray + Burst 잡으로 올라간다.
///
/// **콜라이더가 원본이다. 격자가 아니다.** 경사장갑·콜라이더 크기 덮어쓰기(Placement.size)가
/// 전부 콜라이더 기하에 산다 - "격자와 콜라이더는 다른 층이다" 불변식 그대로.
///
/// Phase 1에서는 판정에 관여하지 않는다: <see cref="VerifyMode"/>를 켜면 파편 레이마다
/// Physics2D 결과와 이 세계의 결과를 대조해 어긋난 것만 로그로 남긴다. Physics2D가
/// 항상 권위다. 어긋남이 0으로 수렴하면 다음 단계에서 권위를 넘긴다.
///
/// 알려진 의도적 차이: Physics2D는 콜라이더 이음매에서 쓰레기 법선을 주고(MinFacing
/// 폴백이 그걸 막는 밴드에이드다) 이 세계는 정확한 면 법선을 준다. 대조에서 법선은
/// 안 본다 - 어차피 파편 판정은 법선을 안 쓴다.
/// </summary>
public static class TraceWorld
{
    /// <summary>대조 모드. Tools > Ballistics > Toggle Trace Verify.</summary>
    public static bool VerifyMode;

    public struct Hit
    {
        public int index;        // _colliders와 같은 줄
        public float distance;
        public Vector2 point;
        public Vector2 normal;
    }

    // 스냅샷. 배열 넷이 같은 줄을 공유한다 - 나중에 NativeArray로 그대로 옮겨지는 모양.
    private struct Entry
    {
        public Vector2 centre;   // 월드
        public Vector2 axisU;    // 단위축 (콜라이더 로컬 x의 월드 방향)
        public Vector2 axisV;
        public float halfU;
        public float halfV;
        public int layer;
        public bool isArmor;   // 동점 규칙용: 겹친 면에서는 판이 탄을 받는다
    }

    private static Entry[] _obb = new Entry[1024];
    private static Collider2D[] _colliders = new Collider2D[1024];
    private static int _count;
    private static long _builtTick = -1;
    private static int _builtEpoch = -1;
    private static int _epoch;
    private static int _skippedNonBox;

    /// <summary>
    /// 스냅샷 무효화. **Simulate 직후에 반드시 한 번** - 틱 시작에 뜬 스냅샷을 탄 페이즈에
    /// 쓰면 배가 이동한 만큼(틱당 ~0.5 m) 전부 어긋난다. 대조에서 그대로 나온 패턴이다.
    /// wave 커밋 뒤에도 부른다(2단계) - "wave 단위 스냅샷"이 이 한 줄로 성립한다.
    /// </summary>
    public static void Invalidate() => _epoch++;

    private static readonly Collider2D[] _attached = new Collider2D[1024];

    /// <summary>
    /// 틱당 한 번, 첫 질의가 부른다. 살아 있는 모든 몸(함선·잔해·운석)의 콜라이더를
    /// OBB로 뜬다. 박스가 아닌 콜라이더는 못 뜨고 센다 - 대조에서 그 콜라이더에 맞은
    /// 레이는 "불일치"가 아니라 "미지원"으로 분류해야 억울한 로그가 안 쌓인다.
    /// </summary>
    private static void BuildIfStale()
    {
        if (_builtTick == Core.TickManager.currentTick && _builtEpoch == _epoch)
            return;

        _builtTick = Core.TickManager.currentTick;
        _builtEpoch = _epoch;
        _count = 0;
        _skippedNonBox = 0;

        List<HullStructure> all = HullStructure.All;

        for (int b = 0; b < all.Count; b++)
        {
            HullStructure body = all[b];

            if (body == null || body.Body == null)
                continue;

            int n = body.Body.GetAttachedColliders(_attached);

            for (int i = 0; i < n; i++)
            {
                Collider2D c = _attached[i];

                if (c == null || !c.enabled)
                    continue;

                if (c is not BoxCollider2D box)
                {
                    _skippedNonBox++;
                    continue;
                }

                if (_count == _obb.Length)
                {
                    System.Array.Resize(ref _obb, _obb.Length * 2);
                    System.Array.Resize(ref _colliders, _colliders.Length * 2);
                }

                Transform t = box.transform;

                // 반전 함선(lossyScale.x = -1)까지 포함해 월드 축을 정확히 뜬다.
                // TransformVector는 회전·스케일을 다 먹으므로 축 방향과 반너비가 같이 나온다.
                Vector2 u = t.TransformVector(new Vector2(box.size.x * 0.5f, 0f));
                Vector2 v = t.TransformVector(new Vector2(0f, box.size.y * 0.5f));

                float hu = u.magnitude;
                float hv = v.magnitude;

                if (hu <= 1e-6f || hv <= 1e-6f)
                    continue;

                _obb[_count] = new Entry
                {
                    centre = t.TransformPoint(box.offset),
                    axisU = u / hu,
                    axisV = v / hv,
                    halfU = hu,
                    halfV = hv,
                    layer = c.gameObject.layer,
                    isArmor = c.TryGetComponent(out Armor _),
                };
                _colliders[_count] = c;
                _count++;
            }
        }
    }

    /// <summary>
    /// 가장 가까운 명중. Physics2D.Raycast의 규약을 따른다:
    /// queriesStartInColliders = false - 시작점을 품은 콜라이더는 안 맞은 것으로 친다.
    /// </summary>
    public static bool Trace(Vector2 start, Vector2 dir, float range, int layerMask, out Hit hit)
    {
        BuildIfStale();

        hit = default;
        float sq = dir.sqrMagnitude;

        if (sq < 1e-12f)
            return false;

        dir /= Mathf.Sqrt(sq);

        float best = float.MaxValue;
        int bestIndex = -1;
        bool bestIsArmor = false;
        Vector2 bestNormal = default;
        const float TieEpsilon = 1e-3f;   // 모듈이 판 면에 딱 붙은 자리의 동점 창

        for (int i = 0; i < _count; i++)
        {
            ref Entry e = ref _obb[i];

            if ((layerMask & (1 << e.layer)) == 0)
                continue;

            // OBB 로컬로: 슬래브 검사
            Vector2 rel = start - e.centre;
            float ru = Vector2.Dot(rel, e.axisU);
            float rv = Vector2.Dot(rel, e.axisV);
            float du = Vector2.Dot(dir, e.axisU);
            float dv = Vector2.Dot(dir, e.axisV);

            // 시작점이 안이면 통째로 무시 - queriesStartInColliders = false.
            // **표면 1mm 안도 "안"이다.** 파편은 방금 맞은 면 위에서 태어난다 - Physics2D는
            // 이 서브밀리 경계에서 미스와 0m 명중을 오락가락했고(대조 잔여 2건 전부 이것),
            // 여기는 규칙이다: 낳아준 면을 도로 맞지 않는다. SpallResolver의 Epsilon 넛지와
            // 같은 의도를 판정 쪽에서 못박는 것.
            const float SurfaceSkin = 1e-3f;

            if (Mathf.Abs(ru) < e.halfU + SurfaceSkin && Mathf.Abs(rv) < e.halfV + SurfaceSkin)
                continue;

            float tMin = 0f;
            float tMax = range;
            int minAxis = 0;      // 0 = u면, 1 = v면
            float minSign = 0f;

            // u 슬래브
            if (Mathf.Abs(du) < 1e-9f)
            {
                if (Mathf.Abs(ru) > e.halfU)
                    continue;
            }
            else
            {
                float inv = 1f / du;
                float t1 = (-e.halfU - ru) * inv;
                float t2 = (e.halfU - ru) * inv;
                float sign = -Mathf.Sign(du);

                if (t1 > t2)
                    (t1, t2) = (t2, t1);

                if (t1 > tMin)
                {
                    tMin = t1;
                    minAxis = 0;
                    minSign = sign;
                }

                tMax = Mathf.Min(tMax, t2);
            }

            // v 슬래브
            if (Mathf.Abs(dv) < 1e-9f)
            {
                if (Mathf.Abs(rv) > e.halfV)
                    continue;
            }
            else
            {
                float inv = 1f / dv;
                float t1 = (-e.halfV - rv) * inv;
                float t2 = (e.halfV - rv) * inv;
                float sign = -Mathf.Sign(dv);

                if (t1 > t2)
                    (t1, t2) = (t2, t1);

                if (t1 > tMin)
                {
                    tMin = t1;
                    minAxis = 1;
                    minSign = sign;
                }

                tMax = Mathf.Min(tMax, t2);
            }

            if (tMin > tMax || tMin <= 0f)
                continue;

            // **동점은 판이 탄을 받는다.** 모듈은 판 위에 볼트로 붙어 면이 겹치므로 같은
            // 거리의 명중이 상시로 나온다 - Physics2D는 내부 순서로 아무거나 줬고, 여기서는
            // 규칙이다: 판이 겉이다.
            bool tie = Mathf.Abs(tMin - best) <= TieEpsilon;

            if (tie ? (bestIsArmor || !e.isArmor) : tMin >= best)
                continue;

            // 스냅샷 뜬 뒤 같은 페이즈 안에서 죽은 콜라이더(유폭 연쇄가 이 창을 상시로
            // 연다) - Physics2D처럼 없는 것으로 친다. 네이티브 검사가 후보에게만 나가고,
            // 2단계의 wave 스냅샷이 이 검사를 구조적으로 대체한다.
            Collider2D live = _colliders[i];

            if (live == null || !live.enabled)
                continue;

            best = tMin;
            bestIndex = i;
            bestIsArmor = e.isArmor;
            bestNormal = (minAxis == 0 ? _obb[i].axisU : _obb[i].axisV) * minSign;
        }

        if (bestIndex < 0)
            return false;

        hit = new Hit
        {
            index = bestIndex,
            distance = best,
            point = start + dir * best,
            normal = bestNormal,
        };

        return true;
    }

    public static Collider2D ColliderAt(int index) => _colliders[index];

    // ---- 대조 ----

    private static int _agreed;
    private static int _disagreed;
    private static int _ties;
    private static int _unsupported;
    private static int _logged;
    private const int MaxLogs = 20;

    /// <summary>
    /// Physics2D가 이미 낸 답과 대조한다. 권위는 언제나 Physics2D 쪽 - 이 함수는 세지만
    /// 판정을 바꾸지 않는다. 어긋남 로그는 20건에서 멈춘다(그 뒤로는 카운트만).
    /// </summary>
    public static void Verify(
        Vector2 start, Vector2 dir, float range, int layerMask, Collider2D physicsHit, float physicsDistance)
    {
        bool mine = Trace(start, dir, range, layerMask, out Hit hit);

        // Physics2D가 맞힌 것이 박스가 아니면 이 세계엔 애초에 없다 - 미지원으로 분류
        if (physicsHit != null && physicsHit is not BoxCollider2D)
        {
            _unsupported++;
            return;
        }

        bool same;

        if (physicsHit == null)
            same = !mine;
        else
            same = mine && ReferenceEquals(ColliderAt(hit.index), physicsHit)
                && Mathf.Abs(hit.distance - physicsDistance) < 0.02f;

        if (same)
        {
            _agreed++;
            return;
        }

        // 거리는 같은데 콜라이더만 다르다 = 겹친 면의 동점. Physics2D의 답이 애초에
        // 임의였던 자리라 불일치가 아니라 "규칙이 갈린 것"이다 - 우리 규칙은 판 우선.
        if (physicsHit != null && mine && Mathf.Abs(hit.distance - physicsDistance) < 0.02f)
        {
            _ties++;
            return;
        }

        _disagreed++;

        if (_logged < MaxLogs)
        {
            _logged++;
            Debug.LogWarning(
                $"[TraceWorld] 불일치 #{_disagreed}: start={start} dir={dir} range={range:F2}\n"
                + $"  Physics2D: {(physicsHit != null ? $"{physicsHit.name} @{physicsDistance:F3}" : "미스")}\n"
                + $"  TraceWorld: {(mine ? $"{ColliderAt(hit.index).name} @{hit.distance:F3}" : "미스")}");
        }
    }

    public static string VerifyReport()
        => $"[TraceWorld] 일치 {_agreed} / 동점(겹친 면, 판 우선 규칙) {_ties} / 불일치 {_disagreed} "
         + $"/ 미지원(비박스) {_unsupported}"
         + (_skippedNonBox > 0 ? $" (스냅샷 제외 비박스 콜라이더 {_skippedNonBox}개)" : "");

    public static void ResetVerify()
    {
        _agreed = 0;
        _disagreed = 0;
        _ties = 0;
        _unsupported = 0;
        _logged = 0;
    }
}
