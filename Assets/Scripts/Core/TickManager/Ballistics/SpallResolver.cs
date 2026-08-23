using System.Collections.Generic;
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
/// 이 분리가 병렬의 전제다: 계산 단계는 상태를 안 바꾸므로 나중에 그대로
/// IJobParallelFor로 나가고, 커밋만 메인 스레드에 남는다.
///
/// **판정의 권위는 TraceWorld다.** Physics2D 레이는 Verify 모드에서 대조용으로만 나간다.
/// MaxSpallDepth는 이제 호출 깊이가 아니라 세대 수 상한이다 - 허용되는 세대 수는 같다.
/// </summary>
public static class SpallResolver
{
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
    private static readonly List<Event> _events = new();

    // 채널 가중치는 이벤트별 배열이 아니라 **평면 버퍼 + offset**이다. 할당이 없고,
    // NativeArray로 옮길 때 이 모양 그대로 간다.
    private static float[] _channels = new float[Ballistics.SubCount * 64];
    private static int _channelFloats;
    private static readonly float[] _channelScratch = new float[Ballistics.SubCount];

    private static long _sequence;
    private static readonly System.Comparison<Event> _byOrder =
        (a, b) => a.orderKey.CompareTo(b.orderKey);

    private static bool _pumping;

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
        });

        if (!_pumping)
            Pump();
    }

    private static void Pump()
    {
        _pumping = true;

        try
        {
            while (_requests.Count > 0)
            {
                int generation = _requests.Peek().generation;

                // ---- 계산: 이 세대 전부, 읽기 전용. 여기가 나중에 잡으로 나가는 몸통이다.
                // 파편 예산(MaxFragmentsPerPump 류)을 넣게 되면 이 while의 조건에 얹는다 -
                // 구조가 큐라 남은 요청은 다음 기회로 자연스럽게 밀린다.
                _events.Clear();
                _channelFloats = 0;

                using (_mBurst.Auto())
                {
                    while (_requests.Count > 0 && _requests.Peek().generation == generation)
                        Compute(_requests.Dequeue());
                }

                // ---- 커밋: orderKey 정렬(= 결정론). 지금은 발급 순서라 항등이지만, 계산이
                // 병렬로 나가면 이 정렬이 커밋 순서의 유일한 근거다.
                using (_mCommit.Auto())
                {
                    _events.Sort(_byOrder);
                    _commitGeneration = generation;

                    try
                    {
                        for (int i = 0; i < _events.Count; i++)
                            Commit(_events[i]);
                    }
                    finally
                    {
                        _commitGeneration = -1;
                    }
                }

                // wave 사이 무효화는 죽음의 깔때기가 한다(Armor 붕괴·시각 잔해가 콜라이더를
                // 끄는 자리에서 직접 Invalidate). 처음엔 여기서 무조건 무효화했는데,
                // 프로파일러가 그 재굽기(펌프 21회 x 콜라이더 1,500개)를 프레임 100ms의
                // 주범으로 지목했다 - 포즈는 Simulate에서만 변하니 죽음만 신고하면 충분하다.
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
                _events.Add(new Event
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

                _events.Add(new Event
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

                _events.Add(new Event
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
