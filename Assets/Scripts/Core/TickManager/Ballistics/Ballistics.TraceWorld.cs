using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// 탄도용 자체 충돌 세계 - 멀티스레딩의 1단계. Physics2D 레이캐스트가 메인 스레드
/// 족쇄라(2D엔 RaycastCommand가 없다), 판·모듈 콜라이더를 틱당 한 번 OBB 배열로
/// 떠서(스냅샷) 선분 교차를 순수 산술로 푼다. 큰 파편 wave에서는 숫자 스냅샷을
/// NativeArray로 옮기고 IJobParallelFor + Burst가 이 교차를 병렬로 푼다.
///
/// **콜라이더가 원본이다. 격자가 아니다.** 경사장갑·콜라이더 크기 덮어쓰기(Placement.size)가
/// 전부 콜라이더 기하에 산다 - "격자와 콜라이더는 다른 층이다" 불변식 그대로.
///
/// 판정의 권위는 이 세계다. <see cref="VerifyMode"/>를 켜면 파편 레이마다 Physics2D와
/// 대조해 어긋난 것만 로그로 남긴다.
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
        public float radius;   // 브로드페이즈용: max(halfU, halfV)의 외접원이면 충분하다
        public int layer;
        public bool isArmor;   // 동점 규칙용: 겹친 면에서는 판이 탄을 받는다
    }

    /// <summary>
    /// 콜라이더에서 한 번만 읽는 로컬 기하. 함선의 이동·회전·반전은 Transform에 남으므로
    /// 매 틱 이 값들을 월드로 변환하기만 하면 현재 OBB가 된다.
    /// </summary>
    private struct Source
    {
        public Collider2D collider;
        public Transform transform;
        public Vector2 halfU;
        public Vector2 halfV;
        public Vector2 offset;
        public int layer;
        public bool isArmor;
    }

    /// <summary>
    /// 워커가 읽는 순수 숫자 스냅샷. Collider2D/Transform 참조를 절대 넣지 않는다.
    /// 파편 Job은 이 배열만 읽고, 결과의 index를 메인 스레드가 _colliders에 다시 대응한다.
    /// </summary>
    internal struct JobEntry
    {
        public float2 centre;
        public float2 axisU;
        public float2 axisV;
        public float halfU;
        public float halfV;
        public float radius;
        public int layer;
        public byte isArmor;
    }

    internal struct JobHit
    {
        public int index;
        public float distance;
        public float2 point;
        public float2 normal;
        public byte found;
    }

    private static Entry[] _obb = new Entry[1024];
    private static Collider2D[] _colliders = new Collider2D[1024];
    private static int _count;
    private static long _builtTick = -1;
    private static int _builtEpoch = -1;
    private static int _epoch;
    private static int _skippedNonBox;

    // 몸/콜라이더 탐색과 GetComponent는 생성 직후 한 번만 한다. Source 순서는 등록 순서로
    // 고정되어 같은 입력의 동점 판정도 결정론적이다. 장갑/모듈 동점은 별도 판 우선 규칙.
    private static readonly List<Source> _sources = new(1024);
    private static readonly List<Collider2D> _nonBoxes = new();
    // 몸 -> 등록 당시의 콜라이더 수. HashSet이면 "등록했다"만 알고 그 뒤에 붙은
    // 콜라이더를 영영 못 본다.
    private static readonly Dictionary<HullStructure, int> _registeredBodies = new();
    private static readonly HashSet<Collider2D> _registeredColliders = new();

    /// <summary>
    /// 스냅샷 무효화. **Simulate 직후에 반드시 한 번** - 틱 시작에 뜬 스냅샷을 탄 페이즈에
    /// 쓰면 배가 이동한 만큼(틱당 ~0.5 m) 전부 어긋난다. 대조에서 그대로 나온 패턴이다.
    /// 같은 틱의 파편 wave들은 이 스냅샷을 공유하고, 생존 여부만 wave 직전에 다시 뜬다.
    /// </summary>
    public static void Invalidate() => _epoch++;

    private static Collider2D[] _attached = new Collider2D[1024];

    /// <summary>
    /// 틱당 한 번, 첫 질의가 부른다. 살아 있는 모든 몸(함선·잔해·운석)의 콜라이더를
    /// OBB로 뜬다. 박스가 아닌 콜라이더는 못 뜨고 센다 - 대조에서 그 콜라이더에 맞은
    /// 레이는 "불일치"가 아니라 "미지원"으로 분류해야 억울한 로그가 안 쌓인다.
    /// </summary>
    private static readonly Unity.Profiling.ProfilerMarker _mBuild = new("TraceWorld.Build");
    private static readonly Unity.Profiling.ProfilerMarker _mRegister = new("TraceWorld.Register");
    private static readonly Unity.Profiling.ProfilerMarker _mPose = new("TraceWorld.Pose");

    private static void BuildIfStale()
    {
        if (_builtTick == Core.TickManager.currentTick && _builtEpoch == _epoch)
            return;

        using var _ = _mBuild.Auto();

        _builtTick = Core.TickManager.currentTick;
        _builtEpoch = _epoch;
        _count = 0;
        _skippedNonBox = 0;

        List<HullStructure> all = HullStructure.All;

        using (_mRegister.Auto())
        {
            // 긴 전투/캠페인에서 파괴된 Unity 참조가 끝없이 남지 않게 드문 압축만 한다.
            if (_registeredBodies.Count > all.Count * 2 + 64)
                RebuildSources(all);
            else
                RegisterNewBodies(all);
        }

        using (_mPose.Auto())
        {
            for (int i = 0; i < _sources.Count; i++)
            {
                Source source = _sources[i];

                if (!TryBuildEntry(source, out Entry entry))
                    continue;

                EnsureSnapshotCapacity();
                _obb[_count] = entry;
                _colliders[_count] = source.collider;
                _count++;
            }

            for (int i = 0; i < _nonBoxes.Count; i++)
            {
                Collider2D collider = _nonBoxes[i];

                if (collider != null && collider.enabled)
                    _skippedNonBox++;
            }
        }
    }

    private static void RegisterNewBodies(List<HullStructure> all)
    {
        for (int i = 0; i < all.Count; i++)
        {
            HullStructure body = all[i];

            if (body == null || body.Body == null)
                continue;

            // **콜라이더 수가 늘면 다시 훑는다.** "이 몸은 이미 등록했다"만 보면 등록 뒤에
            // 붙은 콜라이더가 이 세계에 영영 없고, 증상은 에러가 아니라 "파편이 그 판을
            // 그냥 통과한다"다 - 판정이 Physics2D에서 넘어온 뒤로는 대조 모드에서만 보인다.
            // 수가 주는 것은 무시해도 된다: 판은 죽기만 하고 TryBuildEntry가 죽은 것을 거른다.
            int attached = body.Body.attachedColliderCount;

            if (_registeredBodies.TryGetValue(body, out int had) && had >= attached)
                continue;

            _registeredBodies[body] = attached;
            RegisterBody(body.Body);
        }
    }

    private static void RebuildSources(List<HullStructure> all)
    {
        _sources.Clear();
        _nonBoxes.Clear();
        _registeredBodies.Clear();
        _registeredColliders.Clear();
        RegisterNewBodies(all);
    }

    private static void RegisterBody(Rigidbody2D body)
    {
        int required = body.attachedColliderCount;

        if (required > _attached.Length)
        {
            int capacity = _attached.Length;

            while (capacity < required)
                capacity *= 2;

            System.Array.Resize(ref _attached, capacity);
        }

        int count = body.GetAttachedColliders(_attached);

        for (int i = 0; i < count; i++)
        {
            Collider2D collider = _attached[i];

            // 잔해로 reparent된 콜라이더는 새 Rigidbody에서도 보인다. 전역 중복 제거로
            // 원래 Source 하나를 유지하면 재등록/순서 변화가 없다.
            if (collider == null || !_registeredColliders.Add(collider))
                continue;

            if (TryCreateSource(collider, out Source source))
                _sources.Add(source);
            else
                _nonBoxes.Add(collider);
        }
    }

    private static bool TryCreateSource(Collider2D collider, out Source source)
    {
        source = default;

        if (collider is not BoxCollider2D box)
            return false;

        source = new Source
        {
            collider = collider,
            transform = box.transform,
            halfU = new Vector2(box.size.x * 0.5f, 0f),
            halfV = new Vector2(0f, box.size.y * 0.5f),
            offset = box.offset,
            layer = collider.gameObject.layer,
            isArmor = collider.TryGetComponent(out Armor _),
        };
        return true;
    }

    private static bool TryBuildEntry(in Source source, out Entry entry)
    {
        entry = default;
        Collider2D collider = source.collider;

        if (collider == null || !collider.enabled || source.transform == null)
            return false;

        Transform transform = source.transform;

        // TransformVector는 현재 회전·스케일을 먹는다. 캐시 뒤 이동/회전/좌우 반전돼도
        // OBB는 현재 자세를 따른다.
        Vector2 u = transform.TransformVector(source.halfU);
        Vector2 v = transform.TransformVector(source.halfV);
        float hu = u.magnitude;
        float hv = v.magnitude;

        if (hu <= 1e-6f || hv <= 1e-6f)
            return false;

        entry = new Entry
        {
            centre = transform.TransformPoint(source.offset),
            axisU = u / hu,
            axisV = v / hv,
            halfU = hu,
            halfV = hv,
            radius = Mathf.Sqrt(hu * hu + hv * hv),
            layer = source.layer,
            isArmor = source.isArmor,
        };
        return true;
    }

    private static void EnsureSnapshotCapacity()
    {
        if (_count < _obb.Length)
            return;

        System.Array.Resize(ref _obb, _obb.Length * 2);
        System.Array.Resize(ref _colliders, _colliders.Length * 2);
    }

#if UNITY_EDITOR
    internal static bool CachedSourceSelfTest()
    {
        GameObject go = new("TraceWorld Cached Source Self Test");

        try
        {
            go.layer = 7;
            BoxCollider2D box = go.AddComponent<BoxCollider2D>();
            box.size = new Vector2(2.5f, 1.25f);
            box.offset = new Vector2(0.3f, -0.2f);

            if (!TryCreateSource(box, out Source source))
                return false;

            // Source를 뜬 뒤 자세를 바꾼다. 음수 x는 좌우 반전 회귀까지 함께 고정한다.
            go.transform.SetPositionAndRotation(new Vector3(4f, -3f), Quaternion.Euler(0f, 0f, 37f));
            go.transform.localScale = new Vector3(-1.7f, 0.8f, 1f);

            if (!TryBuildEntry(source, out Entry entry))
                return false;

            Vector2 expectedU = go.transform.TransformVector(source.halfU);
            Vector2 expectedV = go.transform.TransformVector(source.halfV);
            Vector2 expectedCentre = go.transform.TransformPoint(source.offset);

            return Vector2.Distance(entry.centre, expectedCentre) < 1e-5f
                && Vector2.Distance(entry.axisU, expectedU.normalized) < 1e-5f
                && Vector2.Distance(entry.axisV, expectedV.normalized) < 1e-5f
                && Mathf.Abs(entry.halfU - expectedU.magnitude) < 1e-5f
                && Mathf.Abs(entry.halfV - expectedV.magnitude) < 1e-5f
                && entry.layer == 7;
        }
        finally
        {
            Object.DestroyImmediate(go);
        }
    }
#endif

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

        // 브로드페이즈: 사거리 원 밖의 OBB는 슬래브 검사 자체를 안 한다. 거울 링은
        // 지름 120 m에 파편 사거리가 15 m라, 이 한 줄이 후보의 대부분을 자른다.
        float reach = range;

        for (int i = 0; i < _count; i++)
        {
            ref Entry e = ref _obb[i];

            if ((layerMask & (1 << e.layer)) == 0)
                continue;

            // OBB 로컬로: 슬래브 검사
            Vector2 rel = start - e.centre;

            float cull = reach + e.radius;

            if (rel.sqrMagnitude > cull * cull)
                continue;
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

    /// <summary>
    /// 현재 OBB와 생존 상태를 Job용 TempJob 배열로 뜬다. OBB는 틱 스냅샷이고,
    /// active는 wave 직전의 Collider2D 생존 상태라 같은 틱에 먼저 죽은 유령을 거른다.
    /// 호출자는 Job 완료 뒤 두 배열을 반드시 Dispose한다.
    /// </summary>
    internal static void CreateJobSnapshot(
        out NativeArray<JobEntry> entries,
        out NativeArray<byte> active)
    {
        BuildIfStale();

        entries = new NativeArray<JobEntry>(
            _count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
        active = new NativeArray<byte>(
            _count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);

        for (int i = 0; i < _count; i++)
        {
            ref Entry e = ref _obb[i];

            entries[i] = new JobEntry
            {
                centre = new float2(e.centre.x, e.centre.y),
                axisU = new float2(e.axisU.x, e.axisU.y),
                axisV = new float2(e.axisV.x, e.axisV.y),
                halfU = e.halfU,
                halfV = e.halfV,
                radius = e.radius,
                layer = e.layer,
                isArmor = e.isArmor ? (byte)1 : (byte)0,
            };

            Collider2D live = _colliders[i];
            active[i] = live != null && live.enabled ? (byte)1 : (byte)0;
        }
    }

    /// <summary>
    /// Burst Job에서 실행되는 OBB 전수 판정. UnityEngine.Object와 정적 mutable 상태를
    /// 읽지 않으므로 여러 파편이 동시에 호출해도 안전하다.
    /// </summary>
    internal static JobHit TraceJob(
        NativeArray<JobEntry> entries,
        NativeArray<byte> active,
        float2 start,
        float2 dir,
        float range,
        int layerMask)
    {
        JobHit hit = new JobHit { index = -1 };
        float sq = math.lengthsq(dir);

        if (sq < 1e-12f)
            return hit;

        dir *= math.rsqrt(sq);

        float best = float.MaxValue;
        int bestIndex = -1;
        bool bestIsArmor = false;
        float2 bestNormal = default;
        const float TieEpsilon = 1e-3f;
        const float SurfaceSkin = 1e-3f;

        for (int i = 0; i < entries.Length; i++)
        {
            JobEntry e = entries[i];

            if (active[i] == 0 || (layerMask & (1 << e.layer)) == 0)
                continue;

            float2 rel = start - e.centre;
            float cull = range + e.radius;

            if (math.lengthsq(rel) > cull * cull)
                continue;

            float ru = math.dot(rel, e.axisU);
            float rv = math.dot(rel, e.axisV);
            float du = math.dot(dir, e.axisU);
            float dv = math.dot(dir, e.axisV);

            if (math.abs(ru) < e.halfU + SurfaceSkin && math.abs(rv) < e.halfV + SurfaceSkin)
                continue;

            float tMin = 0f;
            float tMax = range;
            int minAxis = 0;
            float minSign = 0f;

            if (math.abs(du) < 1e-9f)
            {
                if (math.abs(ru) > e.halfU)
                    continue;
            }
            else
            {
                float inv = 1f / du;
                float t1 = (-e.halfU - ru) * inv;
                float t2 = (e.halfU - ru) * inv;
                float sign = -math.sign(du);

                if (t1 > t2)
                {
                    float swap = t1;
                    t1 = t2;
                    t2 = swap;
                }

                if (t1 > tMin)
                {
                    tMin = t1;
                    minAxis = 0;
                    minSign = sign;
                }

                tMax = math.min(tMax, t2);
            }

            if (math.abs(dv) < 1e-9f)
            {
                if (math.abs(rv) > e.halfV)
                    continue;
            }
            else
            {
                float inv = 1f / dv;
                float t1 = (-e.halfV - rv) * inv;
                float t2 = (e.halfV - rv) * inv;
                float sign = -math.sign(dv);

                if (t1 > t2)
                {
                    float swap = t1;
                    t1 = t2;
                    t2 = swap;
                }

                if (t1 > tMin)
                {
                    tMin = t1;
                    minAxis = 1;
                    minSign = sign;
                }

                tMax = math.min(tMax, t2);
            }

            if (tMin > tMax || tMin <= 0f)
                continue;

            bool tie = math.abs(tMin - best) <= TieEpsilon;
            bool isArmor = e.isArmor != 0;

            if (tie ? (bestIsArmor || !isArmor) : tMin >= best)
                continue;

            best = tMin;
            bestIndex = i;
            bestIsArmor = isArmor;
            bestNormal = (minAxis == 0 ? e.axisU : e.axisV) * minSign;
        }

        if (bestIndex < 0)
            return hit;

        hit.index = bestIndex;
        hit.distance = best;
        hit.point = start + dir * best;
        hit.normal = bestNormal;
        hit.found = 1;
        return hit;
    }

    public static Collider2D ColliderAt(int index) => _colliders[index];

    // ---- 대조 ----

    private static int _agreed;
    private static int _disagreed;
    private static int _ties;
    private static int _surface;
    private static int _unsupported;
    private static int _logged;
    private const int MaxLogs = 20;

    /// <summary>
    /// Physics2D의 답과 **이미 계산해 둔 우리 답**을 대조한다(다시 트레이스하지 않는다 -
    /// 대조 모드에서 판정이 두 번 돌던 낭비 제거). 판정의 권위는 이제 우리 쪽이고,
    /// 이 함수는 세기만 한다.
    /// </summary>
    public static void Verify(
        Vector2 start, Vector2 dir, float range, int layerMask,
        Collider2D physicsHit, float physicsDistance, bool mine, in Hit hit)
    {

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

        // Physics2D가 표면 바로 위(수 mm)에서 맞았다고 하는 것 = 파편을 낳은 면 위의
        // 그레이징. 우리 규칙(표면 스킨: 낳아준 면을 도로 맞지 않는다)의 의도된 차이다.
        if (physicsHit != null && physicsDistance < 0.005f)
        {
            _surface++;
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
        => $"[TraceWorld] 일치 {_agreed} / 동점(판 우선 규칙) {_ties} / 표면(스킨 규칙) {_surface} "
         + $"/ 불일치 {_disagreed} / 미지원(비박스) {_unsupported}"
         + (_skippedNonBox > 0 ? $" (스냅샷 제외 비박스 콜라이더 {_skippedNonBox}개)" : "");

    public static void ResetVerify()
    {
        _agreed = 0;
        _disagreed = 0;
        _ties = 0;
        _surface = 0;
        _unsupported = 0;
        _logged = 0;
    }
}
