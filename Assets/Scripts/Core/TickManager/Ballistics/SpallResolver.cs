using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// 파편을 실제로 날려서 맞은 것에 피해를 준다. 무엇이 파편을 낳을지는 PenetrationManager가
/// 정하고, 여기는 그걸 집행만 한다.
///
/// **wave 구조다 (멀티스레딩 2단계).** Burst는 요청을 큐에 넣을 뿐이고, 첫 호출자가
/// 펌프를 돌린다: 같은 세대의 파편 전부를 **읽기 전용으로 계산**(판정은 TraceWorld,
/// 채널 가중치는 복사)하고, 큐 순서 그대로 **커밋**(피해 적용)한다. 커밋 중에 태어나는
/// 파편(붕괴·유폭)은 다음 세대 큐로 간다. 같은 wave의 파편들은 서로를 못 본다 -
/// 전부 wave 시작 스냅샷 기준으로 계산되고, 그것이 규칙이다(같은 순간 도착).
///
/// 64개 이상 파편의 OBB 판정은 IJobParallelFor + Burst로 나가고, Unity 오브젝트를
/// 만지는 채널 계산·트레일·피해 커밋만 메인 스레드에 남는다. 작은 wave는 스케줄 비용을
/// 피하려고 순차 경로를 쓴다.
///
/// **판정의 권위는 TraceWorld다.** Physics2D 레이는 Verify 모드에서 대조용으로만 나간다.
/// MaxSpallDepth는 이제 호출 깊이가 아니라 세대 수 상한이다 - 허용되는 세대 수는 같다.
/// </summary>
public static class SpallResolver
{
    private const int ParallelFragmentThreshold = 64;
    private const int ParallelBatchSize = 16;

    private static readonly RaycastHit2D[] _hits = new RaycastHit2D[8];

    private struct Request
    {
        public Vector2 origin;
        public Vector2 direction;
        public float spread;
        public float energy;
        public int count;
        public uint seed;
        public int mask;
        public float caliber;
        public int generation;

        /// <summary>큐에 들어온 틱. 빚을 개수가 아니라 시간으로 재기 위한 것.</summary>
        public long tick;
    }

    private struct FragmentInput
    {
        public float2 start;
        public float2 direction;
        public float range;
        public float energy;
        public int mask;
    }

    /// <summary>
    /// **Strict + 동기 컴파일이 결정론의 값이다.** 기본 FloatMode는 재결합·근사를 허용하고,
    /// 동기 컴파일이 아니면 에디터에서 Burst가 준비되기 전 몇 프레임이 관리 IL로 돌아
    /// **같은 세션 안에서** 답이 갈린다 - 순차 경로(64발 미만)와 워커 경로가 어긋나는
    /// 자리도 같은 이유다. 판정이 튜닝의 근거라 여기서는 속도보다 재현이 먼저다.
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Strict, CompileSynchronously = true)]
    private struct TraceFragmentsJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<FragmentInput> inputs;
        [ReadOnly] public NativeArray<TraceWorld.JobEntry> world;
        [ReadOnly] public NativeArray<byte> active;
        [WriteOnly] public NativeArray<TraceWorld.JobHit> results;

        /// <summary>world는 용량대로 잡혀 있다 - 실제로 쓰는 것은 여기까지.</summary>
        public int worldCount;

        public void Execute(int index)
        {
            FragmentInput input = inputs[index];
            results[index] = TraceWorld.TraceJob(
                world, active, worldCount, input.start, input.direction, input.range, input.mask);
        }
    }

    private enum Kind { Miss, Armor, Module }

    private struct Event
    {
        /// <summary>
        /// 결정론 커밋 순서. 지금(순차 계산)은 발급 순서 그대로라 정렬이 항등이지만,
        /// 계산이 IJobParallelFor로 나가면 워커 완료 순서가 아니라 **이 키 정렬**이
        /// 커밋 순서다 - 스케줄링이 결과에 새는 문을 지금 닫아 둔다.
        /// </summary>
        public long orderKey;

        public Kind kind;
        public Armor armor;
        public IDamageable target;
        public Component targetBody;   // 파괴 검사와 DamageLog용
        public Vector2 at;             // Miss면 끝점, 아니면 명중점
        public float energy;
        public int channelOffset;      // Armor일 때 _channels의 시작 (길이 SubCount)
    }

    private static readonly Queue<Request> _requests = new();

    // 이번 파면의 요청과 이벤트는 평면 배열 + 개수다. 구조체 List는 인덱서마다 경계
    // 검사가 두 번이고 자랄 때 할당한다 - 여기는 파편 수백 개가 한 파면에 몰리는 자리다.
    private static Request[] _waveRequests = new Request[64];
    private static int _waveRequestCount;
    private static Event[] _events = new Event[512];
    private static int _eventCount;

    private static void AddEvent(in Event e)
    {
        if (_eventCount == _events.Length)
            System.Array.Resize(ref _events, _events.Length * 2);

        _events[_eventCount++] = e;
    }

    // 채널 가중치는 이벤트별 배열이 아니라 **평면 버퍼 + offset**이다. 할당이 없고,
    // NativeArray로 옮길 때 이 모양 그대로 간다.
    private static float[] _channels = new float[Ballistics.SubCount * 64];
    private static int _channelFloats;
    private static readonly float[] _channelScratch = new float[Ballistics.SubCount];

    private static long _sequence;
    /// <summary>Array.Sort용 캐시 인스턴스. Comparison 람다는 정렬마다 래퍼를 만든다.</summary>
    private sealed class OrderKeyComparer : IComparer<Event>
    {
        public int Compare(Event a, Event b) => a.orderKey.CompareTo(b.orderKey);
    }

    private static readonly OrderKeyComparer _byOrder = new();

    private static bool _pumping;
    private static int _deferPumpDepth;

    // 틱당 예산. 큐에 남은 파편 수를 같이 세어 밀린 빚이 얼마인지 안다.
    private static long _budgetTick = -1;
    private static int _fragmentsLeft;
    private static int _pendingFragments;

    private static int TakeBudget()
    {
        if (_budgetTick != Core.TickManager.currentTick)
        {
            _budgetTick = Core.TickManager.currentTick;

            // 0이면 예산을 끈다. 예전처럼 한 틱에 다 처리한다.
            _fragmentsLeft = Ballistics.MaxFragmentsPerPump > 0
                ? Ballistics.MaxFragmentsPerPump
                : int.MaxValue;
        }

        return _fragmentsLeft;
    }

    /// <summary>
    /// 지난 틱의 예산에 밀린 파편을 이어 간다. TickManager가 틱 앞에서 한 번 부른다 -
    /// 새 파편이 안 날아오는 틱에도 밀린 것이 처리되어야 큐가 마르지 않는다.
    /// </summary>
    public static void PumpDeferred()
    {
        if (_requests.Count > 0 && !_pumping && _deferPumpDepth == 0)
            Pump();
    }

    internal struct PumpScope : System.IDisposable
    {
        private bool _active;

        internal PumpScope(bool active) => _active = active;

        public void Dispose()
        {
            if (!_active)
                return;

            _active = false;
            EndDeferredPump();
        }
    }

    internal static PumpScope DeferPump()
    {
        _deferPumpDepth++;
        return new PumpScope(true);
    }

    private static void EndDeferredPump()
    {
        Debug.Assert(_deferPumpDepth > 0, "SpallResolver PumpScope가 중복 해제됐다.");
        _deferPumpDepth--;

        if (_deferPumpDepth == 0 && !_pumping && _requests.Count > 0)
            Pump();
    }

    /// <summary>지금 커밋 중인 세대. 커밋이 낳는 파편은 이 다음 세대로 간다. 평시 -1.</summary>
    private static int _commitGeneration = -1;

    public static void Resolve(in HitResult r, LayerMask mask)
    {
        if (r.spallCount <= 0 || r.spallEnergy <= 0f)
            return;

        Burst(
            r.spallOrigin,
            r.spallDirection,
            r.spallSpread,
            r.spallEnergy,
            r.spallCount,
            r.spallSeed,
            mask,
            r.calliber);
    }

    private static readonly Unity.Profiling.ProfilerMarker _mBurst = new("Spall.Burst");
    private static readonly Unity.Profiling.ProfilerMarker _mCommit = new("Spall.Commit");
    private static readonly Unity.Profiling.ProfilerMarker _mJobSchedule = new("Spall.TraceJob.Schedule");
    private static readonly Unity.Profiling.ProfilerMarker _mJobComplete = new("Spall.TraceJob.Complete");

    /// <summary>
    /// 한 점에서 부채꼴로 파편을 뿌린다. 관통 뒤의 스폴도, 무너지는 판의 파편도 전부 이것
    /// 하나다. spread는 반각(도). **여기서는 아무 일도 안 일어난다** - 큐에 넣고, 바깥
    /// 호출이면 펌프가 돌아 이번 틱 안에서 전부 끝난다. 바깥에서 보면 예전과 같이 동기다.
    /// </summary>
    public static void Burst(
        Vector2 origin,
        Vector2 direction,
        float spread,
        float energy,
        int count,
        uint seed,
        LayerMask mask, float caliber = 0f)
    {
        int generation = _commitGeneration + 1;

        if (count <= 0 || energy <= 0f || generation >= Ballistics.MaxSpallDepth)
            return;

        if (direction.sqrMagnitude < 1e-6f)
            direction = Vector2.up;

        _requests.Enqueue(new Request
        {
            origin = origin,
            direction = direction,
            spread = spread,
            energy = energy,
            count = count,
            seed = seed,
            mask = mask.value,
            caliber = caliber,
            generation = generation,
            tick = Core.TickManager.currentTick,
        });

        _pendingFragments += count;

        if (!_pumping && _deferPumpDepth == 0)
            Pump();
    }

    private static void Pump()
    {
        _pumping = true;

        try
        {
            while (_requests.Count > 0)
            {
                // **예산은 틱당이고, 빚은 시간으로 잰다.** 넘친 요청은 큐에 남고 다음 틱이
                // 이어 간다 - 총량은 그대로고 스파이크만 눕는다. 개수로 재면(옛 방식) 큰
                // 폭발 하나가 문턱을 즉시 넘겨서 정작 눕히려던 순간에 예산이 꺼진다.
                bool catchUp = Core.TickManager.currentTick - _requests.Peek().tick
                    > Ballistics.SpallMaxLagTicks;

                if (!catchUp && TakeBudget() <= 0)
                    break;

                int generation = _requests.Peek().generation;

                // ---- 계산: 이 세대 전부, 읽기 전용. 여기가 잡으로 나가는 몸통이다.
                _eventCount = 0;
                _channelFloats = 0;
                _waveRequestCount = 0;

                int fragmentCount = 0;

                while (_requests.Count > 0 && _requests.Peek().generation == generation)
                {
                    // 예산을 다 쓰면 같은 세대라도 여기서 끊는다. **남은 요청은 다음 파면이
                    // 된다** - 그 파편들은 갱신된 스냅샷을 보게 되는데, 애초에 "같은 파면은
                    // 서로를 못 본다"가 규칙이라 같은 종류의 근사다.
                    // 요청 하나는 절대 안 쪼갠다: 최소 하나는 처리해야 전진이 보장된다.
                    if (!catchUp && _waveRequestCount > 0
                        && fragmentCount + _requests.Peek().count > _fragmentsLeft)
                    {
                        break;
                    }

                    Request request = _requests.Dequeue();
                    if (_waveRequestCount == _waveRequests.Length)
                        System.Array.Resize(ref _waveRequests, _waveRequests.Length * 2);

                    _waveRequests[_waveRequestCount++] = request;
                    fragmentCount += request.count;
                }

                _pendingFragments -= fragmentCount;
                _fragmentsLeft -= fragmentCount;

                using (_mBurst.Auto())
                {
                    if (fragmentCount >= ParallelFragmentThreshold)
                        ComputeParallel(fragmentCount);
                    else
                        for (int i = 0; i < _waveRequestCount; i++)
                            Compute(_waveRequests[i]);
                }

                // ---- 커밋: orderKey 정렬(= 결정론). 지금은 발급 순서라 항등이지만, 계산이
                // 병렬로 나가면 이 정렬이 커밋 순서의 유일한 근거다.
                using (_mCommit.Auto())
                {
                    System.Array.Sort(_events, 0, _eventCount, _byOrder);
                    _commitGeneration = generation;

                    try
                    {
                        for (int i = 0; i < _eventCount; i++)
                            Commit(_events[i]);
                    }
                    finally
                    {
                        _commitGeneration = -1;
                    }
                }

                // wave 사이 무효화는 **없다.** 포즈는 Simulate에서만 변하고(그 자리가
                // 무효화한다), 틱 안에서 죽는 콜라이더는 Trace의 채택 시 생존 검사가
                // 걸러낸다. 한때 wave 끝 무조건 무효화(재굽기 폭풍, 프레임 100ms), 그 다음
                // 죽음 깔때기 신고(그라인딩 = 매 틱 재굽기)를 거쳐 여기 도달했다 -
                // 스냅샷 재굽기는 틱당 최대 2회(전반 1 + Simulate 후 1)가 정답이다.
            }
        }
        finally
        {
            _pumping = false;
        }
    }

    /// <summary>파편 하나하나를 판정만 한다. 상태 변경 없음 - 트레일(그림)만 예외다.</summary>
    private static void Compute(in Request request)
    {
        var rng = new DeterministicRng(request.seed);

        float perFragment = request.energy / request.count;
        float range = Mathf.Clamp(
            perFragment * Ballistics.SpallRangePerEnergy,
            Ballistics.SpallRangeMin,
            Ballistics.SpallRangeMax);

        Vector2 direction = request.direction;

        // origin sits exactly on the face that was just hit. Nudge along the spray
        // direction first, or every fragment re-hits that plate at distance 0 and the
        // shell gets paid twice for one penetration.
        for (int i = 0; i < request.count; i++)
        {
            Vector2 right = new Vector2(direction.y, -direction.x);

            // -1 = 왼쪽 가장자리, 0 = 탄두 중심, +1 = 오른쪽 가장자리
            float lateral = rng.Range(-1f, 1f);

            // caliber는 mm, 월드는 m. Armor 붕괴처럼 탄두 단면이 없는 호출은 0을 넘겨
            // 한 점에서 뿌린다.
            Vector2 fragmentOrigin =
                request.origin + right * lateral * (request.caliber * 0.0005f);

            // 중심 0, 가장자리 1
            float edgeFactor = Mathf.Abs(lateral);

            // 중심에서는 좁게, 가장자리에서는 넓게
            float localSpread = Mathf.Lerp(
                request.spread,
                Mathf.Min(180f, request.spread * 10f),
                edgeFactor
            );

            Vector2 d = Ballistics.Rotate(
                direction,
                rng.Range(-localSpread, localSpread)
            );

            Vector2 fragmentStart =
                fragmentOrigin + d * Ballistics.Epsilon;

            // **판정의 권위.** Physics2D는 Verify 모드의 대조용으로만 돈다.
            bool hitSomething = TraceWorld.Trace(
                fragmentStart, d, range, request.mask, out TraceWorld.Hit hit);

            if (TraceWorld.VerifyMode)
            {
                int n = Physics2D.RaycastNonAlloc(fragmentStart, d, _hits, range, request.mask);
                RaycastHit2D pv = n > 0 ? Nearest(n) : default;
                TraceWorld.Verify(fragmentStart, d, range, request.mask,
                    n > 0 ? pv.collider : null, n > 0 ? pv.distance : 0f, hitSomething, in hit);
            }

            if (!hitSomething)
            {
                Vector2 far = fragmentStart + d.normalized * range;

                // 파편이 지나간 선을 화면에 남긴다. 그림뿐이고, 판정에는 관여하지 않는다.
                SpallTrails.Add(fragmentStart, far, SpallTrails.Kind.Miss);

                // 앞판을 하나도 못 맞고 날아갔다는 것은 **가로막은 실물이 없었다**는
                // 뜻이다. 그 끝에 반대편 벽이 있으면 거기 박힌다 - 후면은 콜라이더가
                // 없어서 위 판정에는 애초에 안 잡힌다.
                AddEvent(new Event
                {
                    orderKey = _sequence++,
                    kind = Kind.Miss,
                    at = far,
                    energy = perFragment,
                });
                continue;
            }

            Collider2D col = TraceWorld.ColliderAt(hit.index);

            if (col.TryGetComponent(out Armor armor))
            {
                SpallTrails.Add(fragmentStart, hit.point, SpallTrails.Kind.Armor);

                // 파편도 선이다. 맞은 면의 칸에만 넣으면 6x6 격자의 테두리만 갉히고
                // 안쪽은 영원히 멀쩡하다 - 파편은 언제나 표면에 닿으니까.
                // 채널은 wave 시작 상태에서 계산해 평면 버퍼에 복사한다 - 커밋 순서와 무관하다.
                armor.TraceChannel(
                    hit.point, d, Ballistics.SpallChannelDepth, _channelScratch, out _);

                int offset = ReserveChannel();
                System.Array.Copy(_channelScratch, 0, _channels, offset, Ballistics.SubCount);

                AddEvent(new Event
                {
                    orderKey = _sequence++,
                    kind = Kind.Armor,
                    armor = armor,
                    at = hit.point,
                    energy = perFragment,
                    channelOffset = offset,
                });
            }
            else if (col.TryGetComponent(out IDamageable target))
            {
                SpallTrails.Add(fragmentStart, hit.point, SpallTrails.Kind.Module);

                AddEvent(new Event
                {
                    orderKey = _sequence++,
                    kind = Kind.Module,
                    target = target,
                    targetBody = col,
                    at = hit.point,
                    energy = perFragment,
                });
            }
            else
            {
                // 맞긴 맞았는데 피해를 받는 물건이 아니었다. 선은 거기서 끊긴다.
                SpallTrails.Add(fragmentStart, hit.point, SpallTrails.Kind.Miss);
            }
        }
    }

    /// <summary>
    /// 큰 wave만 실제 워커로 보낸다. 입력 생성과 Unity 오브젝트 커밋은 메인 스레드,
    /// 파편 x OBB 교차의 곱만 IJobParallelFor + Burst가 담당한다.
    /// </summary>
    private static void ComputeParallel(int fragmentCount)
    {
        var inputs = new NativeArray<FragmentInput>(
            fragmentCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
        var results = new NativeArray<TraceWorld.JobHit>(
            fragmentCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);

        // 세계 배열은 TraceWorld가 살려 두는 것이라 여기서 만들지도, 치우지도 않는다.
        TraceWorld.GetJobSnapshot(
            out NativeArray<TraceWorld.JobEntry> world,
            out NativeArray<byte> active,
            out int worldCount);

        try
        {
            int at = 0;

            for (int r = 0; r < _waveRequestCount; r++)
            {
                Request request = _waveRequests[r];
                var rng = new DeterministicRng(request.seed);
                float perFragment = request.energy / request.count;
                float range = Mathf.Clamp(
                    perFragment * Ballistics.SpallRangePerEnergy,
                    Ballistics.SpallRangeMin,
                    Ballistics.SpallRangeMax);
                Vector2 direction = request.direction;

                for (int i = 0; i < request.count; i++)
                {
                    Vector2 right = new Vector2(direction.y, -direction.x);
                    float lateral = rng.Range(-1f, 1f);
                    Vector2 fragmentOrigin =
                        request.origin + right * lateral * (request.caliber * 0.0005f);
                    float edgeFactor = Mathf.Abs(lateral);
                    float localSpread = Mathf.Lerp(
                        request.spread,
                        Mathf.Min(180f, request.spread * 10f),
                        edgeFactor);
                    Vector2 d = Ballistics.Rotate(
                        direction,
                        rng.Range(-localSpread, localSpread));
                    Vector2 start = fragmentOrigin + d * Ballistics.Epsilon;

                    inputs[at++] = new FragmentInput
                    {
                        start = new float2(start.x, start.y),
                        direction = new float2(d.x, d.y),
                        range = range,
                        energy = perFragment,
                        mask = request.mask,
                    };
                }
            }

            var job = new TraceFragmentsJob
            {
                inputs = inputs,
                world = world,
                active = active,
                worldCount = worldCount,
                results = results,
            };

            JobHandle handle;

            using (_mJobSchedule.Auto())
                handle = job.Schedule(fragmentCount, ParallelBatchSize);

            using (_mJobComplete.Auto())
                handle.Complete();

            // 워커 완료 순서와 무관하게 입력 0..N 순서로만 이벤트를 발급한다.
            for (int i = 0; i < fragmentCount; i++)
            {
                FragmentInput input = inputs[i];
                TraceWorld.JobHit result = results[i];
                TraceWorld.Hit hit = new TraceWorld.Hit
                {
                    index = result.index,
                    distance = result.distance,
                    point = new Vector2(result.point.x, result.point.y),
                    normal = new Vector2(result.normal.x, result.normal.y),
                };

                ProcessParallelResult(in input, result.found != 0, in hit);
            }
        }
        finally
        {
            results.Dispose();
            inputs.Dispose();
        }
    }

#if UNITY_EDITOR
    /// <summary>
    /// 예산의 위험은 느려지는 것이 아니라 **큐가 안 마르는 것**이다. 한 틱에 다 못 하고,
    /// 다음 틱에 이어 가고, 언젠가 반드시 끝나는 세 가지를 못 박는다.
    /// (콜라이더가 하나도 없는 편집 모드라 모든 파편이 빗나가고 커밋은 빈 몸 순회다.)
    /// </summary>
    internal static bool FragmentBudgetSelfTest()
    {
        _requests.Clear();
        _pendingFragments = 0;
        _budgetTick = -1;

        int over = Ballistics.MaxFragmentsPerPump * 3;

        using (DeferPump())
        {
            for (int i = 0; i < over; i += 8)
            {
                Burst(Vector2.zero, Vector2.up, 10f, 80f, 8, (uint)(i + 1), 0);
            }
        }
        // 스코프가 끝나며 한 번 펌프했다. 예산이 있으니 전부는 못 했어야 한다.
        bool stoppedEarly = _requests.Count > 0 && _pendingFragments > 0;
        int afterFirst = _pendingFragments;

        // 다음 틱: 예산이 다시 차고 더 나아간다.
        _budgetTick = -1;
        PumpDeferred();

        bool madeProgress = _pendingFragments < afterFirst;

        // 그리고 언젠가 끝난다. 틱을 넉넉히 굴려도 안 마르면 그게 버그다.
        int guard = 0;

        while (_requests.Count > 0 && guard++ < 64)
        {
            _budgetTick = -1;
            PumpDeferred();
        }

        bool drained = _requests.Count == 0 && _pendingFragments == 0;

        _budgetTick = -1;
        return stoppedEarly && madeProgress && drained;
    }

    /// <summary>메뉴 셀프테스트가 실제 Job 스케줄과 결과 슬롯 순서를 검증한다.</summary>
    internal static bool TraceJobSelfTest()
    {
        var world = new NativeArray<TraceWorld.JobEntry>(2, Allocator.TempJob);
        var active = new NativeArray<byte>(2, Allocator.TempJob);
        var inputs = new NativeArray<FragmentInput>(3, Allocator.TempJob);
        var results = new NativeArray<TraceWorld.JobHit>(3, Allocator.TempJob);

        try
        {
            world[0] = new TraceWorld.JobEntry
            {
                centre = new float2(5f, 0f),
                axisU = new float2(1f, 0f),
                axisV = new float2(0f, 1f),
                halfU = 0.5f,
                halfV = 0.5f,
                radius = 0.7072f,
                layer = 0,
                isArmor = 1,
            };
            world[1] = new TraceWorld.JobEntry
            {
                centre = new float2(8f, 0f),
                axisU = new float2(1f, 0f),
                axisV = new float2(0f, 1f),
                halfU = 0.5f,
                halfV = 0.5f,
                radius = 0.7072f,
                layer = 1,
            };
            active[0] = 1;
            active[1] = 1;

            inputs[0] = new FragmentInput
            {
                start = float2.zero,
                direction = new float2(1f, 0f),
                range = 10f,
                mask = 3,
            };
            inputs[1] = new FragmentInput
            {
                start = float2.zero,
                direction = new float2(1f, 0f),
                range = 10f,
                mask = 2,
            };
            inputs[2] = new FragmentInput
            {
                start = float2.zero,
                direction = new float2(0f, 1f),
                range = 10f,
                mask = 3,
            };

            new TraceFragmentsJob
            {
                inputs = inputs,
                world = world,
                active = active,
                worldCount = world.Length,
                results = results,
            }.Schedule(3, 1).Complete();

            return results[0].found != 0 && results[0].index == 0
                && math.abs(results[0].distance - 4.5f) < 1e-4f
                && results[1].found != 0 && results[1].index == 1
                && math.abs(results[1].distance - 7.5f) < 1e-4f
                && results[2].found == 0;
        }
        finally
        {
            results.Dispose();
            inputs.Dispose();
            active.Dispose();
            world.Dispose();
        }
    }
#endif

    private static void ProcessParallelResult(
        in FragmentInput input,
        bool hitSomething,
        in TraceWorld.Hit hit)
    {
        Vector2 fragmentStart = new Vector2(input.start.x, input.start.y);
        Vector2 d = new Vector2(input.direction.x, input.direction.y);

        if (TraceWorld.VerifyMode)
        {
            int n = Physics2D.RaycastNonAlloc(fragmentStart, d, _hits, input.range, input.mask);
            RaycastHit2D pv = n > 0 ? Nearest(n) : default;
            TraceWorld.Verify(fragmentStart, d, input.range, input.mask,
                n > 0 ? pv.collider : null, n > 0 ? pv.distance : 0f, hitSomething, in hit);
        }

        if (!hitSomething)
        {
            Vector2 far = fragmentStart + d.normalized * input.range;
            SpallTrails.Add(fragmentStart, far, SpallTrails.Kind.Miss);
            AddEvent(new Event
            {
                orderKey = _sequence++,
                kind = Kind.Miss,
                at = far,
                energy = input.energy,
            });
            return;
        }

        Collider2D col = TraceWorld.ColliderAt(hit.index);

        if (col.TryGetComponent(out Armor armor))
        {
            SpallTrails.Add(fragmentStart, hit.point, SpallTrails.Kind.Armor);
            armor.TraceChannel(
                hit.point, d, Ballistics.SpallChannelDepth, _channelScratch, out _);

            int offset = ReserveChannel();
            System.Array.Copy(_channelScratch, 0, _channels, offset, Ballistics.SubCount);
            AddEvent(new Event
            {
                orderKey = _sequence++,
                kind = Kind.Armor,
                armor = armor,
                at = hit.point,
                energy = input.energy,
                channelOffset = offset,
            });
        }
        else if (col.TryGetComponent(out IDamageable target))
        {
            SpallTrails.Add(fragmentStart, hit.point, SpallTrails.Kind.Module);
            AddEvent(new Event
            {
                orderKey = _sequence++,
                kind = Kind.Module,
                target = target,
                targetBody = col,
                at = hit.point,
                energy = input.energy,
            });
        }
        else
        {
            SpallTrails.Add(fragmentStart, hit.point, SpallTrails.Kind.Miss);
        }
    }

    /// <summary>
    /// 피해 적용. 같은 wave의 앞선 커밋이 이미 죽인 대상은 조용히 넘어간다 -
    /// 죽은 판의 ApplyDamageAlong은 _collapsed 가드가, 파괴된 모듈은 유니티 null이 거른다.
    /// </summary>
    private static void Commit(in Event e)
    {
        switch (e.kind)
        {
            case Kind.Miss:
                HullStructure.SpallRear(e.at, e.energy);
                break;

            case Kind.Armor:
                if (e.armor != null)
                {
                    // ApplyDamageAlong은 배열 처음부터 SubCount개를 읽으므로 평면 버퍼의
                    // 조각을 스크래치로 꺼내 넘긴다. 커밋은 순차라 스크래치 하나면 된다.
                    System.Array.Copy(_channels, e.channelOffset, _channelScratch, 0, Ballistics.SubCount);
                    e.armor.ApplyDamageAlong(_channelScratch, e.energy);
                }
                break;

            case Kind.Module:
                if (e.targetBody != null)
                {
                    e.target.TakeDamage(e.energy);
                    DamageLog.Hit(e.targetBody.transform, e.energy, e.target);
                }
                break;
        }
    }

    private static int ReserveChannel()
    {
        if (_channelFloats + Ballistics.SubCount > _channels.Length)
            System.Array.Resize(ref _channels, _channels.Length * 2);

        int offset = _channelFloats;
        _channelFloats += Ballistics.SubCount;
        return offset;
    }

    private static RaycastHit2D Nearest(int count)
    {
        int best = 0;

        for (int i = 1; i < count; i++)
        {
            if (_hits[i].distance < _hits[best].distance)
                best = i;
        }

        return _hits[best];
    }
}
