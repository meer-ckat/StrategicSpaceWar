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
        public Transform transform;
        public Vector2 halfU;
        public Vector2 halfV;
        public Vector2 offset;
        public int layer;
        public bool isArmor;
    }

    private struct BodyGeometry
    {
        public Vector2 halfU;
        public Vector2 halfV;
        public Vector2 offset;
    }

    private sealed class HullSourceCache
    {
        public Rigidbody2D body;
        public Collider2D[] colliders = new Collider2D[8];
        public int[] sourceIndices = new int[8];
        public BodyGeometry[] bodyGeometry = new BodyGeometry[8];
        public int attachedCount = -1;
        public int count;
        public int bodyLocalCount;
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

    internal struct JobHull
    {
        public float2 min;
        public float2 max;
        public int start;
        public int count;
    }

    private struct HullEntry
    {
        public Vector2 min;
        public Vector2 max;
        public int start;
        public int count;
    }

    private static HullEntry[] _hulls = new HullEntry[64];
    private static int _hullCount;
    private static readonly Dictionary<Collider2D, int> _sourceIndex = new();

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
    // **List가 아니라 평범한 배열이다.** List 인덱서는 메서드라 struct를 복사해서 돌려주고,
    // 틱당 콜라이더 수천 번이면 그 복사만으로 값이 나간다. 배열 원소는 주소를 가진 변수라
    // `in` 인자에 그대로 묶인다 - 복사가 없다.
    //
    // NativeArray는 여기 못 쓴다. Source가 Transform 참조를 들고 있고 네이티브 컨테이너는
    // unmanaged 타입만 받는다 - 이 배열은 Burst로 갈 수 없는 층이다.
    private static Source[] _sources = new Source[1024];
    private static int _sourceCount;
    private static readonly List<Collider2D> _nonBoxes = new();
    // 몸별 연결은 콜라이더 수가 바뀔 때만 다시 뜬다. 첫 스냅샷 뒤에는 파괴·재부모화만
    // 있으므로 수가 같으면 연결도 같다.
    private static readonly Dictionary<HullStructure, HullSourceCache> _hullSources = new();
    private static HullSourceCache[] _activeHullSources = new HullSourceCache[64];
    private static int _activeHullSourceCount;
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
        long tick = Core.TickManager.currentTick;
        int epoch = _epoch;

        if (_builtTick == tick && _builtEpoch == epoch)
            return;

        using var _ = _mBuild.Auto();

        _count = 0;
        _skippedNonBox = 0;

        List<HullStructure> all = HullStructure.All;

        using (_mRegister.Auto())
        {
            // 긴 전투/캠페인에서 파괴된 Unity 참조가 끝없이 남지 않게 드문 압축만 한다.
            if (_hullSources.Count > all.Count * 2 + 64)
                RebuildSources();

            SyncHullSources(all);
        }

        using (_mPose.Auto())
        {
            _hullCount = 0;
            EnsureHullCapacity(_activeHullSourceCount);

            for (int h = 0; h < _activeHullSourceCount; h++)
            {
                HullSourceCache cache = _activeHullSources[h];

                if (cache.count <= 0)
                    continue;

                int start = _count;
                EnsureSnapshotCapacity(_count + cache.count);

                Matrix4x4 bodyToWorld = cache.bodyLocalCount > 0
                    ? cache.body.transform.localToWorldMatrix
                    : default;

                Vector2 min = new(
                    float.PositiveInfinity,
                    float.PositiveInfinity);

                Vector2 max = new(
                    float.NegativeInfinity,
                    float.NegativeInfinity);

                for (int c = 0; c < cache.count; c++)
                {
                    int sourceIndex = cache.sourceIndices[c];
                    ref Source source = ref _sources[sourceIndex];
                    Entry entry;

                    bool built = source.isArmor
                        ? TryBuildBodyEntry(
                            in source,
                            in cache.bodyGeometry[c],
                            in bodyToWorld,
                            out entry)
                        : TryBuildEntry(in source, out entry);

                    if (!built)
                        continue;

                    _obb[_count] = entry;
                    _colliders[_count] = cache.colliders[c];

                    // OBB를 감싸는 월드 AABB
                    float extentX =
                        Mathf.Abs(entry.axisU.x) * entry.halfU +
                        Mathf.Abs(entry.axisV.x) * entry.halfV;

                    float extentY =
                        Mathf.Abs(entry.axisU.y) * entry.halfU +
                        Mathf.Abs(entry.axisV.y) * entry.halfV;

                    Vector2 extent = new(extentX, extentY);

                    min = Vector2.Min(
                        min,
                        entry.centre - extent);

                    max = Vector2.Max(
                        max,
                        entry.centre + extent);

                    _count++;
                }

                int colliderCount = _count - start;

                if (colliderCount <= 0)
                    continue;

                _hulls[_hullCount++] = new HullEntry
                {
                    min = min,
                    max = max,
                    start = start,
                    count = colliderCount
                };
            }

            if (VerifyMode)
            {
                for (int i = 0; i < _nonBoxes.Count; i++)
                {
                    Collider2D collider = _nonBoxes[i];

                    if (collider != null && collider.enabled)
                        _skippedNonBox++;
                }
            }
        }

        _builtTick = tick;
        _builtEpoch = epoch;
    }

    private static void SyncHullSources(List<HullStructure> all)
    {
        int previousActiveCount = _activeHullSourceCount;
        _activeHullSourceCount = 0;
        EnsureActiveHullCapacity(all.Count);

        for (int i = 0; i < all.Count; i++)
        {
            HullStructure hull = all[i];

            if (hull == null || hull.Body == null)
                continue;

            Rigidbody2D body = hull.Body;
            int attached = body.attachedColliderCount;

            if (!_hullSources.TryGetValue(hull, out HullSourceCache cache))
            {
                cache = new HullSourceCache();
                _hullSources.Add(hull, cache);
            }

            if (!ReferenceEquals(cache.body, body) || cache.attachedCount != attached)
                RebuildHullSources(cache, body, attached);

            _activeHullSources[_activeHullSourceCount++] = cache;
        }

        if (_activeHullSourceCount < previousActiveCount)
        {
            System.Array.Clear(
                _activeHullSources,
                _activeHullSourceCount,
                previousActiveCount - _activeHullSourceCount);
        }
    }

    private static void RebuildSources()
    {
        // 참조를 지워서 파괴된 Collider2D/Transform이 배열에 매달려 있지 않게 한다.
        System.Array.Clear(_sources, 0, _sourceCount);
        _sourceCount = 0;

        _nonBoxes.Clear();
        _hullSources.Clear();
        _registeredColliders.Clear();
        _sourceIndex.Clear();
    }

    private static void RebuildHullSources(
        HullSourceCache cache,
        Rigidbody2D body,
        int required)
    {
        EnsureAttachedCapacity(required);

        int count = body.GetAttachedColliders(_attached);
        EnsureHullSourceCapacity(cache, count);

        int previousCount = cache.count;
        cache.body = body;
        cache.attachedCount = count;
        cache.count = 0;
        cache.bodyLocalCount = 0;

        Matrix4x4 worldToBody = default;
        bool hasWorldToBody = false;

        for (int i = 0; i < count; i++)
        {
            Collider2D collider = _attached[i];

            if (ReferenceEquals(collider, null)
                || !TryGetOrRegisterSource(collider, out int sourceIndex))
                continue;

            int destination = cache.count++;
            cache.colliders[destination] = collider;
            cache.sourceIndices[destination] = sourceIndex;

            ref Source source = ref _sources[sourceIndex];

            if (source.isArmor)
            {
                if (!hasWorldToBody)
                {
                    worldToBody = body.transform.worldToLocalMatrix;
                    hasWorldToBody = true;
                }

                cache.bodyGeometry[destination] = CreateBodyGeometry(
                    in source,
                    in worldToBody);
                cache.bodyLocalCount++;
            }
        }

        if (cache.count < previousCount)
            System.Array.Clear(cache.colliders, cache.count, previousCount - cache.count);
    }

    private static bool TryGetOrRegisterSource(Collider2D collider, out int sourceIndex)
    {
        if (_sourceIndex.TryGetValue(collider, out sourceIndex))
            return true;

        if (!_registeredColliders.Add(collider))
        {
            sourceIndex = -1;
            return false;
        }

        if (!TryCreateSource(collider, out Source source))
        {
            _nonBoxes.Add(collider);
            sourceIndex = -1;
            return false;
        }

        if (_sourceCount >= _sources.Length)
            System.Array.Resize(ref _sources, _sources.Length * 2);

        sourceIndex = _sourceCount++;
        _sources[sourceIndex] = source;
        _sourceIndex.Add(collider, sourceIndex);
        return true;
    }

    private static bool TryCreateSource(Collider2D collider, out Source source)
    {
        source = default;

        if (collider is not BoxCollider2D box)
            return false;

        source = new Source
        {
            transform = box.transform,
            halfU = new Vector2(box.size.x * 0.5f, 0f),
            halfV = new Vector2(0f, box.size.y * 0.5f),
            offset = box.offset,
            layer = collider.gameObject.layer,
            isArmor = collider.TryGetComponent(out Armor _),
        };
        return true;
    }

    private static BodyGeometry CreateBodyGeometry(
        in Source source,
        in Matrix4x4 worldToBody)
    {
        Matrix4x4 sourceToWorld = source.transform.localToWorldMatrix;

        return new BodyGeometry
        {
            halfU = worldToBody.MultiplyVector(sourceToWorld.MultiplyVector(source.halfU)),
            halfV = worldToBody.MultiplyVector(sourceToWorld.MultiplyVector(source.halfV)),
            offset = worldToBody.MultiplyPoint3x4(sourceToWorld.MultiplyPoint3x4(source.offset)),
        };
    }

    private static bool TryBuildEntry(in Source source, out Entry entry)
    {
        // 몸에 대해 움직일 수 있는 모듈만 이 길로 온다. 고정 장갑은 몸-로컬 기하를 캐시해
        // 선체당 행렬 하나로 TryBuildBodyEntry에서 처리한다.
        //
        // enabled를 여기서 안 본다: 죽은 콜라이더는 **채택할 때** 걸러진다(Trace의 생존
        // 검사와 GetJobSnapshot의 active 배열). 여기서 한 번 더 보는 것은 같은 답을 두 번
        // 사는 것이고, 남는 엔트리는 hull AABB를 조금 부풀릴 뿐이라 보수적으로만 틀린다.

        // TransformVector/TransformPoint를 따로 부르면 같은 행렬을 세 번 네이티브에서
        // 받아온다. 한 번 받아 C#에서 곱하면 산술은 같고 마샬링만 사라진다.
        Matrix4x4 toWorld = source.transform.localToWorldMatrix;

        return TryBuildEntry(
            in source,
            in toWorld,
            source.halfU,
            source.halfV,
            source.offset,
            out entry);
    }

    private static bool TryBuildBodyEntry(
        in Source source,
        in BodyGeometry geometry,
        in Matrix4x4 bodyToWorld,
        out Entry entry)
        => TryBuildEntry(
            in source,
            in bodyToWorld,
            geometry.halfU,
            geometry.halfV,
            geometry.offset,
            out entry);

    private static bool TryBuildEntry(
        in Source source,
        in Matrix4x4 toWorld,
        Vector2 halfU,
        Vector2 halfV,
        Vector2 offset,
        out Entry entry)
    {
        entry = default;

        // 현재 회전·스케일을 먹는다. 캐시 뒤 이동/회전/좌우 반전돼도 OBB는 현재 자세를 따른다.
        Vector2 u = toWorld.MultiplyVector(halfU);
        Vector2 v = toWorld.MultiplyVector(halfV);
        float hu = u.magnitude;
        float hv = v.magnitude;

        if (hu <= 1e-6f || hv <= 1e-6f)
            return false;

        entry = new Entry
        {
            centre = toWorld.MultiplyPoint3x4(offset),
            axisU = u * (1f / hu),
            axisV = v * (1f / hv),
            halfU = hu,
            halfV = hv,
            radius = Mathf.Sqrt(hu * hu + hv * hv),
            layer = source.layer,
            isArmor = source.isArmor,
        };
        return true;
    }

    private static void EnsureAttachedCapacity(int required)
    {
        if (required <= _attached.Length)
            return;

        int capacity = _attached.Length;

        while (capacity < required)
            capacity *= 2;

        System.Array.Resize(ref _attached, capacity);
    }

    private static void EnsureHullSourceCapacity(HullSourceCache cache, int required)
    {
        if (required <= cache.colliders.Length)
            return;

        int capacity = cache.colliders.Length;

        while (capacity < required)
            capacity *= 2;

        System.Array.Resize(ref cache.colliders, capacity);
        System.Array.Resize(ref cache.sourceIndices, capacity);
        System.Array.Resize(ref cache.bodyGeometry, capacity);
    }

    private static void EnsureActiveHullCapacity(int required)
    {
        if (required <= _activeHullSources.Length)
            return;

        int capacity = _activeHullSources.Length;

        while (capacity < required)
            capacity *= 2;

        System.Array.Resize(ref _activeHullSources, capacity);
    }

    private static void EnsureHullCapacity(int required)
    {
        if (required <= _hulls.Length)
            return;

        int capacity = _hulls.Length;

        while (capacity < required)
            capacity *= 2;

        System.Array.Resize(ref _hulls, capacity);
    }

    private static void EnsureSnapshotCapacity(int required)
    {
        if (required <= _obb.Length)
            return;

        int capacity = _obb.Length;

        while (capacity < required)
            capacity *= 2;

        System.Array.Resize(ref _obb, capacity);
        System.Array.Resize(ref _colliders, capacity);
    }

#if UNITY_EDITOR
    internal static bool CachedSourceSelfTest()
    {
        GameObject fixture = new("TraceWorld Cached Source Self Test");

        try
        {
            var bodyA = new GameObject("body A");
            bodyA.transform.SetParent(fixture.transform, false);

            var bodyB = new GameObject("body B");
            bodyB.transform.SetParent(fixture.transform, false);

            var plate = new GameObject("plate");
            plate.transform.SetParent(bodyA.transform, false);
            plate.layer = 7;
            plate.transform.localPosition = new Vector3(1.5f, -0.75f, 0f);
            plate.transform.localRotation = Quaternion.Euler(0f, 0f, 19f);
            plate.transform.localScale = new Vector3(0.8f, 1.2f, 1f);

            BoxCollider2D box = plate.AddComponent<BoxCollider2D>();
            box.size = new Vector2(2.5f, 1.25f);
            box.offset = new Vector2(0.3f, -0.2f);

            if (!TryCreateSource(box, out Source source))
                return false;

            BodyGeometry bodyGeometry = CreateBodyGeometry(
                in source,
                bodyA.transform.worldToLocalMatrix);

            bodyA.transform.SetPositionAndRotation(
                new Vector3(4f, -3f),
                Quaternion.Euler(0f, 0f, 37f));
            bodyA.transform.localScale = new Vector3(-1.7f, 0.8f, 1f);

            Matrix4x4 bodyToWorld = bodyA.transform.localToWorldMatrix;

            if (!TryBuildBodyEntry(
                    in source,
                    in bodyGeometry,
                    in bodyToWorld,
                    out Entry bodyEntry)
                || !EntryMatches(in source, plate.transform, in bodyEntry))
                return false;

            // 포탑처럼 몸에 대해 도는 모듈은 현재 Transform을 계속 읽는다.
            plate.transform.localRotation = Quaternion.Euler(0f, 0f, -42f);

            if (!TryBuildEntry(in source, out Entry dynamicEntry)
                || !EntryMatches(in source, plate.transform, in dynamicEntry))
                return false;

            plate.transform.SetParent(bodyB.transform, worldPositionStays: true);
            bodyGeometry = CreateBodyGeometry(
                in source,
                bodyB.transform.worldToLocalMatrix);

            bodyB.transform.SetPositionAndRotation(
                new Vector3(-6f, 2f),
                Quaternion.Euler(0f, 0f, -23f));
            bodyB.transform.localScale = new Vector3(1.25f, -0.9f, 1f);
            bodyToWorld = bodyB.transform.localToWorldMatrix;

            return TryBuildBodyEntry(
                    in source,
                    in bodyGeometry,
                    in bodyToWorld,
                    out Entry reboundEntry)
                && EntryMatches(in source, plate.transform, in reboundEntry);
        }
        finally
        {
            Object.DestroyImmediate(fixture);
        }
    }

    private static bool EntryMatches(
        in Source source,
        Transform transform,
        in Entry entry)
    {
        Vector2 expectedU = transform.TransformVector(source.halfU);
        Vector2 expectedV = transform.TransformVector(source.halfV);
        Vector2 expectedCentre = transform.TransformPoint(source.offset);

        return Vector2.Distance(entry.centre, expectedCentre) < 1e-5f
            && Vector2.Distance(entry.axisU, expectedU.normalized) < 1e-5f
            && Vector2.Distance(entry.axisV, expectedV.normalized) < 1e-5f
            && Mathf.Abs(entry.halfU - expectedU.magnitude) < 1e-5f
            && Mathf.Abs(entry.halfV - expectedV.magnitude) < 1e-5f
            && entry.layer == 7;
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

        // 브로드페이즈 1단계: hull AABB. 거울 링(776판)처럼 콜라이더가 한 hull에 몰린
        // 덩어리가 이 광선의 사거리 밖이면 그 안의 콜라이더를 전부 건너뛴다 - 파편
        // Job(TraceJob, 아래)이 이미 쓰던 hull 단계를 일반 포탄 경로에도 그대로 쓴다.
        // 예전엔 일반 포탄이 이 단계 없이 _count 전체(전장의 모든 콜라이더)를 돌았다.
        float reach = range;

        float2 startF = new(start.x, start.y);
        float2 dirF = new(dir.x, dir.y);

        for (int h = 0; h < _hullCount; h++)
        {
            HullEntry hull = _hulls[h];

            if (!RayIntersectsAabb(
                    startF, dirF, range,
                    new float2(hull.min.x, hull.min.y),
                    new float2(hull.max.x, hull.max.y)))
                continue;

            int hullEnd = hull.start + hull.count;

            // 브로드페이즈 2단계: hull을 통과한 것만 사거리 원(기존 로직 그대로)으로 거른다.
            for (int i = hull.start; i < hullEnd; i++)
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
    // 워커가 읽는 배열은 **살려 둔다.** 파면마다 새로 만들면 세계 전체(OBB 1,500개)를
    // 파면마다 다시 복사하게 되고, 파편 예산이 파면 수를 늘리는 순간 그 고정비가 그대로
    // 렉이 된다 - 스파이크를 눕히려다 총량을 늘린 자리가 여기였다.
    private static NativeArray<JobEntry> _jobWorld;
    private static NativeArray<byte> _jobActive;

    private static NativeArray<JobHull> _jobHulls;
    private static long _jobWorldTick = -1;
    private static int _jobWorldEpoch = -1;
    internal static void GetJobSnapshot(
    out NativeArray<JobEntry> entries,
    out NativeArray<byte> active,
    out int count,
    out NativeArray<JobHull> hulls,
    out int hullCount)
    {
        BuildIfStale();

        // 세계 배열은 _count까지 쓴다. 이 블록이 없으면 두 배열이 default NativeArray로
        // 남아 첫 병렬 파면에서 NRE가 난다 - hull 배열만 잡고 이쪽을 지웠던 자리다.
        if (!_jobWorld.IsCreated || _jobWorld.Length < _count)
        {
            if (_jobWorld.IsCreated)
                _jobWorld.Dispose();

            if (_jobActive.IsCreated)
                _jobActive.Dispose();

            int capacity = Mathf.Max(
                256,
                Mathf.NextPowerOfTwo(Mathf.Max(1, _count)));

            _jobWorld = new NativeArray<JobEntry>(
                capacity,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);

            _jobActive = new NativeArray<byte>(
                capacity,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);

            // 새 배열이면 기하를 다시 채워야 한다
            _jobWorldTick = -1;
        }

        if (!_jobHulls.IsCreated || _jobHulls.Length < _hullCount)
        {
            if (_jobHulls.IsCreated)
                _jobHulls.Dispose();

            int capacity = Mathf.Max(
                16,
                Mathf.NextPowerOfTwo(Mathf.Max(1, _hullCount)));

            _jobHulls = new NativeArray<JobHull>(
                capacity,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);

            // 새 NativeArray이므로 복사 다시 하게
            _jobWorldTick = -1;
        }

        // 기하는 스냅샷이 바뀔 때만. 틱당 최대 두 번이고 파면 수와 무관하다.
        if (_jobWorldTick != _builtTick || _jobWorldEpoch != _epoch)
        {
            _jobWorldTick = _builtTick;
            _jobWorldEpoch = _epoch;

            for (int i = 0; i < _count; i++)
            {
                ref Entry e = ref _obb[i];

                _jobWorld[i] = new JobEntry
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
            }
        }

        // 생존만 파면마다 다시 본다. 틱 안에서 판이 죽고, 그것이 스냅샷을 무효화하지
        // 않는다는 것이 이 세계의 규칙이라(죽은 것은 채택할 때 거른다) 여기가 그 자리다.
        for (int i = 0; i < _count; i++)
        {
            Collider2D live = _colliders[i];
            _jobActive[i] = live != null && live.enabled ? (byte)1 : (byte)0;
        }
        for (int i = 0; i < _hullCount; i++)
        {
            HullEntry h = _hulls[i];

            _jobHulls[i] = new JobHull
            {
                min = new float2(h.min.x, h.min.y),
                max = new float2(h.max.x, h.max.y),
                start = h.start,
                count = h.count
            };
        }

        entries = _jobWorld;
        active = _jobActive;
        count = _count;

        hulls = _jobHulls;
        hullCount = _hullCount;
    }

    private static void DisposeJobSnapshot()
    {
        if (_jobWorld.IsCreated)
            _jobWorld.Dispose();

        if (_jobActive.IsCreated)
            _jobActive.Dispose();

        if (_jobHulls.IsCreated)
            _jobHulls.Dispose();

        _jobWorldTick = -1;
        _jobWorldEpoch = -1;
    }

    // 영속 NativeArray는 주인이 치워야 한다. 플레이 종료·도메인 리로드 둘 다에서.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void InstallDisposal()
    {
        DisposeJobSnapshot();
        Application.quitting -= DisposeJobSnapshot;
        Application.quitting += DisposeJobSnapshot;
    }

#if UNITY_EDITOR
    [UnityEditor.InitializeOnLoadMethod]
    private static void InstallEditorDisposal()
    {
        UnityEditor.AssemblyReloadEvents.beforeAssemblyReload -= DisposeJobSnapshot;
        UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += DisposeJobSnapshot;
    }
#endif

    private static bool RayIntersectsAabb(
        float2 origin,
        float2 dir,
        float range,
        float2 min,
        float2 max)
    {
        float tMin = 0f;
        float tMax = range;

        for (int axis = 0; axis < 2; axis++)
        {
            float o = origin[axis];
            float d = dir[axis];
            float lo = min[axis];
            float hi = max[axis];

            if (math.abs(d) < 1e-8f)
            {
                if (o < lo || o > hi)
                    return false;

                continue;
            }

            float inv = 1f / d;

            float t1 = (lo - o) * inv;
            float t2 = (hi - o) * inv;

            if (t1 > t2)
            {
                float temp = t1;
                t1 = t2;
                t2 = temp;
            }

            tMin = math.max(tMin, t1);
            tMax = math.min(tMax, t2);

            if (tMin > tMax)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Burst Job에서 실행되는 OBB 전수 판정. UnityEngine.Object와 정적 mutable 상태를
    /// 읽지 않으므로 여러 파편이 동시에 호출해도 안전하다.
    /// </summary>
   internal static JobHit TraceJob(
    NativeArray<JobEntry> entries,
    NativeArray<byte> active,
    NativeArray<JobHull> hulls,
    int hullCount,
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

                for (int h = 0; h < hullCount; h++)
        {
            JobHull hull = hulls[h];

            if (!RayIntersectsAabb(
                    start,
                    dir,
                    range,
                    hull.min,
                    hull.max))
            {
                continue;
            }

            int end = hull.start + hull.count;

            for (int i = hull.start; i < end; i++)
            {
                JobEntry e = entries[i];

                if (active[i] == 0 ||
                    (layerMask & (1 << e.layer)) == 0)
                    continue;

                float2 rel = start - e.centre;
                float cull = range + e.radius;

                if (math.lengthsq(rel) > cull * cull)
                    continue;

                float ru = math.dot(rel, e.axisU);
                float rv = math.dot(rel, e.axisV);
                float du = math.dot(dir, e.axisU);
                float dv = math.dot(dir, e.axisV);

                if (math.abs(ru) < e.halfU + SurfaceSkin &&
                    math.abs(rv) < e.halfV + SurfaceSkin)
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
                    float t2 = ( e.halfU - ru) * inv;
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
                    float t2 = ( e.halfV - rv) * inv;
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

                if (tie
                    ? (bestIsArmor || !isArmor)
                    : tMin >= best)
                {
                    continue;
                }

                best = tMin;
                bestIndex = i;
                bestIsArmor = isArmor;
                bestNormal =
                    (minAxis == 0 ? e.axisU : e.axisV) * minSign;
            }
        }

        // ★ 모든 Hull을 다 검사한 다음 결과 확정
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
