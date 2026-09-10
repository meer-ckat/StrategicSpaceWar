using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 충각. 물리가 이미 계산해 준 충격량을 받아서, 부딪힌 판에 관통 시스템과 같은 눈금의
/// 피해를 넣는다. 그 뒤로는 포탄에 맞았을 때와 완전히 같은 길을 탄다 - 서브셀이 죽고,
/// 죽은 서브셀이 파편을 뿌리고, 파편이 모듈을 친다.
///
/// Ship과 Hulk(잔해·운석·폐위성)가 같이 쓴다. 예전에는 Ship에만 있어서 잔해가 체력 무한인 벽이었다 -
/// 함선은 잔해에 부딪혀 자기 판만 깎였고, 잔해는 흠집 하나 안 났다.
/// </summary>
public static class RamImpact
{
    /// <summary>
    /// 충각 디버그 로그. 켜면 부딪힌 속도·예산·맞은 판 수·소진량을 찍는다.
    ///
    /// 이 값들을 한 줄에 같이 봐야 원인이 갈린다: 예산이 큰데 소진이 작으면 레이가 목표를
    /// 못 맞힌 것이고, 소진이 판 수에 비해 크면 엉뚱한 판(예전에는 자기 뱃머리)을 먹고 있는 것이다.
    /// </summary>
    public static bool RamLog = false;

    /// <summary>
    /// 충각이 쓸고 지나가는 판. 배 폭만큼 넓은 원으로 훑으므로 자기 뱃머리도 잔뜩 들어온다
    /// (IsChildOf로 거른다). 폭 18 m x 깊이 40 m면 최악에 수백 장이라 넉넉히 잡는다.
    /// </summary>
    private static readonly RaycastHit2D[] _punch = new RaycastHit2D[512];

    /// <summary>
    /// 이번 스윕이 실제로 닿은 **서로 다른** 판. <see cref="_punch"/>를 그대로 세면 안 된다 -
    /// Rigidbody2D.Cast는 (내 콜라이더 x 상대 콜라이더) 쌍마다 결과를 주므로, 뱃머리 판 셋이
    /// 같은 상대 판 하나를 향하면 그 판이 세 번 들어온다.
    ///
    /// 안 걸러내면 두 가지가 틀린다: 충돌 풀이 같은 판 값을 여러 번 치러서 배가 과하게
    /// 느려지고, 접촉 판 수가 부풀어서 뾰족하게 댄 것이 넓게 댄 것으로 계산된다.
    ///
    /// 정적이어도 안전하다 - Punch는 OnTick에서만 불리고 재진입하지 않는다. 루프 안의
    /// 유폭 연쇄는 Conduct/Radiate로 갈 뿐 여기로 돌아오지 않는다.
    /// </summary>
    private static readonly List<Armor> _plates = new();

    /// <summary>
    /// <see cref="_plates"/>와 같은 순서로 그 판이 달린 몸. 히트가 이미 들고 있는 값이라
    /// 여기서 담아 두면 나중에 다시 찾을 일이 없다. 정적 콜라이더면 null이다.
    /// </summary>
    private static readonly List<Rigidbody2D> _plateBodies = new();

    // 같은 판이 여러 번 들어오는 것을 거르는 도장. HashSet이면 스윕 결과(최대 512개)마다
    // 해싱하는데, 충각은 몸마다 매 틱 돈다.
    private static int _punchStamp;

    /// <summary>
    /// <see cref="FarthestReach"/>가 이 몸의 콜라이더를 받아 오는 자리. 함선 한 척이 판
    /// 300장이고 모듈까지 붙으므로 넉넉히 잡는다. Punch가 OnTick에서만 불리고 재진입하지
    /// 않으므로 정적이어도 안전하다 - Conduct/Radiate와 달리 이 안에서 피해가 안 나간다.
    /// </summary>
    private static readonly Collider2D[] _attached = new Collider2D[1024];

    // 접점마다 새로 만들면 한 번 부딪힐 때 최대 16쌍이 쓰레기가 된다. 충각은 난전에서
    // 매 틱 들어온다.
    // 평면 배열 + head/tail. 파면은 판마다 최대 한 번 입큐라 되감기가 필요 없고,
    // Queue<T>의 버전 검사·용량 조정이 통째로 빠진다. 도달 표시는 Armor.ConductStamp
    // 도장이라 집합 자체가 없어졌다 - 간선마다 해싱하던 것이 int 비교 하나가 된다.
    private static Armor[] _wave = new Armor[1024];
    private static int _conductStamp;

    // **재진입 버퍼는 깊이별로 살려 둔다.** 유폭 연쇄는 한 번 터질 때마다 안쪽으로
    // 다시 들어오는데, 그때마다 새 배열을 만들면(파면 버퍼 + 질의 버퍼 1,152칸 = 9 KB)
    // 폭발 하나가 수십 KB를 남긴다. 깊이는 MaxDetonationChain으로 막혀 있으니
    // 깊이마다 한 벌씩만 있으면 된다.
    private static readonly List<Armor[]> _nestedWave = new();
    private static readonly List<HashSet<Armor>> _nestedReached = new();
    private static readonly List<Collider2D[]> _nestedNearby = new();

    private static Armor[] NestedWave(int depth, int minimum)
    {
        while (_nestedWave.Count <= depth)
            _nestedWave.Add(new Armor[1024]);

        if (_nestedWave[depth].Length < minimum)
            _nestedWave[depth] = new Armor[Mathf.NextPowerOfTwo(minimum)];

        return _nestedWave[depth];
    }

    private static HashSet<Armor> NestedReached(int depth)
    {
        while (_nestedReached.Count <= depth)
            _nestedReached.Add(new HashSet<Armor>());

        HashSet<Armor> set = _nestedReached[depth];
        set.Clear();
        return set;
    }

    private static Collider2D[] NestedNearby(int depth)
    {
        while (_nestedNearby.Count <= depth)
            _nestedNearby.Add(new Collider2D[NearbyCapacity]);

        return _nestedNearby[depth];
    }


    // Ship.Ram self가 뭉쳐 보여서 가르는 마커. 릴리스에선 no-op.
    private static readonly Unity.Profiling.ProfilerMarker _mGather = new("Ram.Gather");
    private static readonly Unity.Profiling.ProfilerMarker _mSweep = new("Ram.Sweep");
    private static readonly Unity.Profiling.ProfilerMarker _mConduct = new("Ram.Conduct");

    /// <summary>
    /// 지금 <see cref="Conduct"/> 안인가. 0보다 크면 재진입이고, 그때만 지역 버퍼를 만든다.
    /// </summary>
    private static int _conducting;

    /// <summary>
    /// 유폭이 빈 공간으로 건너갈 때 쓰는 질의 버퍼. **BlastRadius를 따라가야 한다** -
    /// 반경 13.4 m 원이 덮는 1 m 칸이 약 566개고, 모듈 콜라이더까지 들어오므로 두 배 잡는다.
    /// 그래도 꽉 차면 Radiate가 경고한다 - 조용히 자르지 않는다.
    /// </summary>
    private const int NearbyCapacity = 1152;

    private static readonly Collider2D[] _nearby = new Collider2D[NearbyCapacity];

    /// <summary>
    /// 지금 <see cref="Radiate"/> 안인가. <see cref="_conducting"/>과 같은 이유로 있다 -
    /// 피해를 넣다가 다른 탄약고가 터지면 같은 정적 버퍼에 질의가 다시 쓰인다.
    /// </summary>
    private static int _radiating;

    /// <summary>
    /// 충각. **충돌을 기다리지 않는다. 매 틱 앞을 쓸어서 부술 수 있는 것을 미리 없앤다.**
    ///
    /// 솔버는 반발계수 0에서 상대 법선속도를 전부 없애고, 그 충격량을 정하는 것은 두 몸의
    /// 질량뿐이다 - 재료 강도는 식에 안 들어간다. 그래서 유리에 박든 500mm 장갑에 박든 배가
    /// 똑같이 선다. 솔버에는 "판이 몸통에서 뜯긴다"는 개념이 없다.
    ///
    /// 접촉을 받은 **뒤에** 부수는 길은 전부 막다른 길이다. 뒤에서 운동량을 되돌려주면 없던
    /// 에너지를 만들게 되고, 되돌려도 솔버가 이미 꺾어 놓은 각도가 한 프레임 남는다. 두꺼운
    /// 유리를 지나가는 동안 매 틱 새 접촉이 나면 그 한 프레임이 계속 보인다.
    ///
    /// 그래서 접촉을 아예 안 만든다. 예산은 이 몸의 운동에너지고, 이번 틱에 나아가는 만큼의
    /// 얇은 한 겹만 앞에서부터 값을 치르며 지운다. 예산이 떨어지면 거기서 멈추고, 남은 판은
    /// 솔버가 제대로 막아 준다 - **그 정지는 옳은 정지다.**
    ///
    /// 양쪽이 각자 자기 예산으로 부른다. 이중 계산이 아니다: 정면 충돌은 두 배가 각자
    /// 운동에너지를 가져오고, 멈춰 있는 운석은 RamMinSpeed에 걸려 아무것도 안 한다.
    /// </summary>
    /// <param name="thrust">
    /// 지금 걸고 있는 추력(N, 월드). 없으면 zero.
    ///
    /// **운동에너지만으로는 "밀어서 깨기"를 표현할 수 없다.** 유리에 배를 대고 가속하면
    /// 솔버가 붙잡아서 속도가 안 오르고, 속도가 없으면 예산도 없어서 영영 안 깨진다.
    /// 힘 × 거리를 더하면 그 상황이 예산을 갖는다 - 정지해 있어도 스윕은 RamSkin만큼
    /// 나아가려 하므로 일이 0이 아니다.
    /// </param>
    public static void Punch(Transform root, Rigidbody2D body, Vector2 thrust)
    {
        Vector2 velocity = body.linearVelocity;
        float speed = velocity.magnitude;
        bool pushing = thrust.sqrMagnitude > 1f;

        // **중심 속도만 보면 휘두르는 동작이 통째로 사라진다.** 접촉점의 실제 속도는
        // v + ω x r이고, lance처럼 코가 중심에서 30 m 떨어진 배는 각속도 15도/초에서
        // 접선속도가 7.9 m/s다 - RamMinSpeed보다 큰데 지금까지 0으로 세어졌다.
        float omega = body.angularVelocity * Mathf.Deg2Rad;      // rad/s

        // 반경은 캐시다. 매 틱 콜라이더 300개의 bounds를 다시 재는 게 잔해 구름의 틱
        // 비용 대부분이었다. 상한이라 스윕이 약간 길 수 있는데, 아래 접점별 점속도 필터가
        // 초과분을 걸러내므로 부술 판은 같다.
        float rMax = CachedRadius(body);

        // 이번 틱에 이 몸의 **어느 점이든** 나아갈 수 있는 최대 거리. 스윕 길이의 상한이다.
        float reach = speed + Mathf.Abs(omega) * rMax;

        if (reach < Ballistics.RamMinSpeed && !pushing)
            return;

        // **여기까지 온 몸만 산다.** worldCenterOfMass는 네이티브 호출이라, 위 조기 리턴
        // 전에 구해두면 안 밀리는 잔해·정박한 배 전부가 매 틱 쓰지도 않을 값을 낸다 -
        // 이 함수는 그런 몸에서 초 단위로 도는데, 위 reach 계산은 centre를 안 쓴다.
        Vector2 centre = body.worldCenterOfMass;

        // 서 있으면 진행 방향이 없다. 도는 중이면 제일 먼 점의 접선이 곧 휘두르는 쪽이고,
        // 그것도 없으면 밀고 있는 쪽이 파고드는 쪽이다.
        Vector2 dir;

        if (speed > 1e-3f)
            dir = velocity / speed;
        else if (Mathf.Abs(omega) * rMax > 1e-3f)
        {
            // 제자리 회전일 때만 정확한 최원점이 필요하다. 이 분기는 희귀해서 여기서만
            // 전체 콜라이더를 돈다.
            FarthestReach(body, centre, out Vector2 farPoint);
            dir = (Ballistics.Rotate(farPoint - centre, 90f) * Mathf.Sign(omega)).normalized;
        }
        else
            dir = thrust.normalized;

        if (dir.sqrMagnitude < 0.5f)
            return;

        // **몸 전체를 진짜 모양대로 쓴다.** Rigidbody2D.Cast는 이 몸에 달린 콜라이더를 전부
        // 그대로 밀어 보고 무엇에 닿는지 알려준다 - 마스트는 마스트 모양으로, 선체는 선체
        // 모양으로. 자기 콜라이더는 결과에서 알아서 빠진다.
        //
        // 예전에는 반폭짜리 원 하나로 쓸었는데, 배는 원이 아니라서 **절대 닿지 않을 옆구리
        // 바깥까지 지웠다.** 원격으로 터지는 것처럼 보인 것이 그것이다.
        //
        // 거리는 이번 틱에 나아가는 만큼이다. 그 앞의 얇은 한 겹만 지워야 구멍이 배를
        // 따라 자란다.
        float dt = Core.TickManager.TickDeltaTime;

        // 딱 이번 틱에 지나갈 거리다. Punch는 힘 단계에서, 즉 Simulate보다 먼저 도므로
        // 솔버가 접촉을 잡을 때는 이미 치워져 있다 - 앞질러 볼 이유가 없다.
        float lead = dt * Ballistics.RamLookahead;
        float step = reach * lead + Ballistics.RamSkin;

        // 사거리 안에 남의 몸이 없으면 스윕할 것도 없다. 부술 수 있는 것(Armor)은 전부
        // HullStructure의 몸에 붙어 있으므로 후보는 All 목록뿐이다 - 포격전 거리에서는
        // 충각이 이 float 비교 몇 번으로 끝난다.
        if (!GatherNearBodies(body, centre, rMax + step, velocity, Mathf.Abs(omega) * rMax, pushing))
            return;

        // body.Cast는 내 콜라이더 300개를 **전부** 스윕한다(ColliderCastAll). 이번 틱에
        // 닿을 수 있는 건 남의 몸 반경 + step 안에 있는 앞면 몇 장뿐이라, 그것만 골라
        // 하나씩 캐스트하고 거리순으로 합친다 - 아래 루프의 "거리순으로 온다" 가정이
        // 이 정렬로 유지된다.
        // 회전으로 쓸리는 거리. capsule 검사가 이만큼 반경을 부풀려서, 도는 몸이 옆구리로
        // 후려치는 판을 안 놓친다. 병진만 있으면 0이라 순수 capsule이다.
        float swing = Mathf.Abs(omega) * rMax * lead;

        int n = SweepNearColliders(body, dir, step, swing);

        if (n == 0)
            return;

        // **두 예산은 성격이 다르다.**
        //
        // 충돌(운동에너지)은 **총량 풀**이다. 앞에서부터 판 값을 치르며 파고든다 - 한 장을
        // 부수면 그만큼 줄고, 떨어지면 거기서 멈춘다.
        //
        // 압력은 **판마다 따로**다. 압력은 힘 / 면적이고, 접촉 면적은 캐스트가 맞힌 판의
        // 수다(칸이 1 m라 판 수가 곧 접촉 폭). 넓은 면으로 밀면 판당 몫이 작아서 아무것도
        // 안 죽고 상대는 밀려나기만 한다. 뾰족한 뱃머리로 밀면 그 힘이 한 장에 전부 간다.
        // 풀로 만들면 이게 사라진다 - 넓게 대나 좁게 대나 앞의 한 장이 다 먹는다.
        float scale = Ballistics.DamageScale * Ballistics.RamDamageFraction;

        _plates.Clear();
        _plateBodies.Clear();
        int stamp = ++_punchStamp;

        // 위에서 이미 잰 값이다 - Punch 안에서 물리 스텝이 안 도는 한 안 움직인다.
        Vector2 where = centre;

        // 첫 접촉까지의 실제 거리. 캐스트는 한 틱 앞을 미리 보므로 접촉점이 내 선체보다
        // 한참 앞에 있을 수 있고, 그때 이 값이 없으면 내 뱃머리를 못 찾는다.
        float reachedAt = 0f;

        Rigidbody2D hitBody = null;

        // 거리순으로 오므로 앞에서부터 담긴다. 중복은 첫 번째(제일 가까운) 것만 남는다.
        for (int i = 0; i < n; i++)
        {
            Collider2D probe = _punch[i].collider;

            if (probe == null || !probe.TryGetComponent(out Armor plate) || plate == null)
                continue;

            if (plate.PunchStamp == stamp)
                continue;

            plate.PunchStamp = stamp;

            // **스윕 거리는 상한이지 이 점의 거리가 아니다.** step은 제일 빠른 점(회전이면
            // 제일 먼 점) 기준이라, 중심 근처의 판까지 그만큼 앞을 지운다. 예전에 반폭짜리
            // 원으로 쓸어서 "원격으로 터지는 것처럼 보인" 것과 같은 실수다.
            //
            // 그래서 점마다 진짜 속도로 다시 본다. 회전하는 몸에서는 코가 닿는 동안 허리는
            // 아직 멀리 있고, 그것이 맞다.
            Vector2 arm = _punch[i].point - centre;
            Vector2 pointVelocity = velocity + Ballistics.Rotate(arm, 90f) * omega;

            // 캐스트와 **같은 lead**를 쓴다. 여기만 dt로 두면 늘린 스윕을 도로 걸러낸다.
            if (Vector2.Dot(pointVelocity, dir) * lead + Ballistics.RamSkin < _punch[i].distance)
                continue;

            if (_plates.Count == 0)
            {
                where = _punch[i].point;
                reachedAt = _punch[i].distance;

                // 운동량을 넘겨줄 몸. 접촉이 여럿이어도 첫(제일 가까운) 것이 실제로
                // 부딪히는 몸이고, where가 이미 그 점이라 미는 자리와 짝이 맞는다.
                hitBody = _punch[i].rigidbody;
            }

            _plates.Add(plate);
            _plateBodies.Add(_punch[i].rigidbody);
        }

        int contacts = _plates.Count;

        if (contacts == 0)
            return;

        // 같은 충각 틱이 낳는 붕괴 파편은 한 물리 사건이다. 판/서브셀마다 즉시 Pump하면
        // 작은 Job 184개가 되고 더 느려진다. 이 스코프 끝에서 요청을 한 wave로 묶어
        // 큰 IJobParallelFor 하나로 보낸다. 직접 충각 피해 순서는 아래 코드 그대로다.
        using var spallBatch = SpallResolver.DeferPump();

        // **충돌 에너지는 상대속도로 잰다. 절대속도가 아니다.**
        //
        // 같은 속도로 나란히 가는 두 몸은 서로 안 부딪힌다 - 상대속도가 0이니까. 그런데
        // 여기서 절대속도를 쓰면 **선체 안에 갇힌 잔해가 배에 실려 다니면서** 배 속도만큼의
        // 운동에너지로 매 틱 안쪽 판을 갉는다. 30 m/s로 항행하면 그 잔해는 30 m/s로 들이받는
        // 것으로 세어지고, 밖으로 밀어낼 수도 없어서(사방이 판이다) 영원히 끝나지 않는다.
        // 증상이 "잔해가 배 안에서 나뒹굴면 특히 치명적"이었다.
        //
        // 상대속도로 재면 그 상황이 저절로 0이 된다. 실려 가는 것은 부딪히는 것이 아니다.
        float relSpeed = hitBody != null && hitBody.bodyType == RigidbodyType2D.Dynamic
            ? (velocity - hitBody.linearVelocity).magnitude
            : speed;

        // **회전 운동에너지도 예산이다.** 여기서만 진짜 관성 모멘트를 쓴다 - Ship.Angle은
        // 각속도를 직접 대입하고 관성 모멘트를 angleAccel에 녹여 두었지만, 그건 조종 모델의
        // 사정이고 에너지는 리지드바디가 콜라이더에서 뽑아 둔 body.inertia가 진실이다.
        float linear = 0.5f * body.mass * relSpeed * relSpeed;
        float spin = 0.5f * body.inertia * omega * omega;
        float motion = linear + spin;

        // **압착 예산. step이 아니라 dt를 곱한다.**
        //
        // 이 값은 압력(힘/면적)이 아니라 이번 틱의 **압착 충격량**(힘 x 시간)이다. 압착
        // 피해율이 접촉력에 비례한다고 보면 `접촉력 x dt`가 그 틱의 몫이고, 아래에서 판
        // 수로 나누는 것이 면적 몫이다.
        //
        // 예전 step은 더 나빴다. `step = reach x lead + RamSkin`이라 **정지 상태에서도
        // RamSkin(0.04)이 남는다** - 시간 기반조차 아니었고 물리 틱마다 상수를 먹였다.
        // 틱레이트를 바꾸면 초당 압착 피해가 같이 바뀌는, 장갑 강도가 프레임 예산에
        // 달린 시스템이었다. dt를 곱하면 초당 누적이 `(F x dt) x (1/dt) = F`로 떨어져
        // "같은 추력으로 1초 누르면 같은 만큼 찌그러진다"가 성립한다.
        //
        // 속도가 섞이던 것도 같이 사라진다 - step은 속도에 비례했는데, 속도로 부수는
        // 몫은 위 충돌 풀이 이미 따로 센다. step을 쓰면 그 속도를 두 번 냈다.
        float press = Mathf.Max(0f, Vector2.Dot(thrust, dir)) * dt;

        float pressEach = press * Ballistics.RamPressureDamageScale / contacts;

        // 한 틱에 쏟을 수 있는 몫만 들고 들어간다. 예산 전부를 한 틱에 태우면 못 뚫는 벽에서
        // 속도가 0으로 떨어지고, 그러면 솔버가 접촉도 회전도 못 만든다.
        //
        // 질량 무릎: 가벼운 몸은 KE를 다 못 쓴다. 작은 잔해가 파편 구름 값을 하게 깎는
        // 자리다 - 감속 블록은 pool이 아니라 fromMotion(실제 쓴 몫)을 되돌리므로,
        // 여기서 깎이면 잔해가 그만큼 덜 느려지는 것까지 맞아떨어진다.
        float knee = body.mass / (body.mass + Ballistics.RamMassKnee);
        float pool = motion * scale * Ballistics.RamSpendPerTick * knee;
        float left = pool;

        Armor target = null;

        // 실제로 전달된 압착의 합. 예산(press)이 아니라 결과다 - 항복 문턱과 질량비가
        // 깎고 남은 것만 들어온다.
        float crushed = 0f;

        for (int i = 0; i < contacts; i++)
        {
            Armor plate = _plates[i];

            // 앞 판이 죽으면서 유폭이 나면 뒤 판이 같이 사라질 수 있다.
            if (plate == null)
                continue;

            // **질량비는 두 예산에 다 걸리고, 판마다 본다.**
            //
            // 비탄성 충돌에서 변형에 쓰이는 에너지는 `KE x m_other/(M+m)`뿐이다. 나머지는
            // 상대가 튕겨나가면서 그대로 들고 간다. 이 항이 없으면 구축함이 202 kg 잔해
            // 한 장에 운동에너지의 절반을 쏟아붓고, 그 잔해를 **밀지 못하고 먹어 치우면서**
            // 자기 속도를 다 잃는다.
            //
            // 첫 히트 하나로 정하면 안 되는 이유도 같다 - 정적 콜라이더가 먼저 걸리거나
            // 순서가 어긋나면 가벼운 파편이 1.0을 받는다.
            float react = Reaction(body, _plateBodies[i]);

            // **압착에는 항복 문턱이 있다.** 이 판이 그냥 견디는 몫을 빼고 넘은 것만 낸다.
            //
            // 없으면 살짝 대고만 있어도 시간이 알아서 선체를 뚫는다 - 실제 재료는 항복
            // 응력 아래에서 영구 변형이 0이고, 우주선으로 운석을 밀어 옮기는 그림이
            // 성립하는 이유가 그것이다. 문턱을 판 체력에 비례시켜서 두꺼운 장갑이 더
            // 버티는 것이 상수 하나로 나온다.
            //
            // **충돌 몫에는 안 건다.** 저건 운동에너지 풀이라 쓰면 줄어서 스스로 끝나고,
            // RamMinSpeed가 이미 아래쪽을 막고 있다. 여기만 끝나는 조건이 없었다.
            float crush = Mathf.Max(
                0f, pressEach * react - plate.PlateHp * Ballistics.RamCrushYield);

            // **되받는 것은 실제로 전달된 압착이다.** 접촉력은 양쪽에 똑같이 걸리므로
            // 상대가 안 먹은 몫을 내가 먹을 수는 없다. 예전에는 press 원값을 그대로
            // 되받아서, 900 kg 운석을 미는 구축함이 react 0.0002 탓에 **상대의 5000배를
            // 자기 뱃머리에 냈다** - 운석은 항복 문턱에 막혀 0을 먹는데. 증상이
            // "한 천체를 계속 밀면 계속 피해를 입는다"였고, 문턱을 상대 쪽에만 걸었을 때
            // 절반만 고쳐진 것이 이 줄이다. spent는 Conduct에도 가므로 전도까지 같이
            // 부풀어 있었다.
            crushed += crush;

            float share = crush;

            // 충돌 몫은 앞에서부터. 이 판이 속한 몸에 실제로 전달할 수 있는 만큼만 꺼낸다.
            // 판을 확실히 죽이는 값이 PlateHp이고, 모자라면 그만큼만 넣고 지나간다.
            if (left > 0f)
            {
                float take = Mathf.Min(left * react, plate.PlateHp);
                share += take;
                left -= take;
            }

            if (share <= 0f)
                continue;

            plate.ApplyDamageEvenly(share);
            DamageLog.Ram(plate);

            target ??= plate;
        }

        float fromMotion = pool - left;
        float spent = fromMotion + crushed;

        if (RamLog)
            Debug.Log(
                $"[RAM] v={speed:F1} w={body.angularVelocity:F1}도/s rMax={rMax:F1} "
                + $"reach={reach:F1} 접촉={contacts}장 충돌풀={pool:F0}(쓴 {fromMotion:F0}, "
                + $"선형 {linear / Mathf.Max(1e-6f, motion):P0}) 압착예산={press * Ballistics.RamPressureDamageScale:F1}(판당 {pressEach:F2}, 전달 {crushed:F1}) "
                + $"step={step:F2}", body);

        if (spent <= 0f)
            return;

        // **에너지 예산이 먼저 깎고, 충격량이 그 위에 얹힌다.** 회전만 여기서 깎는다 -
        // 병진 몫은 아래에서 접촉점 충격량으로 나가므로 두 번 내면 안 된다. 순서가
        // 뒤바뀌면 더 나쁘다: 이 블록은 angularVelocity에 **대입**하므로 아래
        // AddForceAtPosition이 만든 회전을 통째로 덮어쓴다.
        float joules = motion > 0f
            ? fromMotion / (Ballistics.DamageScale * Ballistics.RamDamageFraction)
            : 0f;

        // **쓴 만큼을 두 운동에서 각각 뺀다.** 예산을 합쳐서 냈으니 청구서도 나눠 물린다 -
        // 회전에만 물리면 직진으로 박은 배가 멀쩡히 계속 가고, 반대면 도는 힘으로 부순
        // 배가 영영 안 느려진다. 몫은 각자가 예산에 넣은 비율 그대로다.
        if (joules > 0f && Mathf.Abs(omega) > 1e-4f && body.inertia > 1e-4f)
        {
            float paidSpin = joules * (spin / motion);

            float slowedSpin = Mathf.Sqrt(
                Mathf.Max(0f, omega * omega - 2f * paidSpin / body.inertia));

            body.angularVelocity = slowedSpin * Mathf.Sign(omega) * Mathf.Rad2Deg;
        }

        // **부수기만 하고 밀지는 않고 있었다.** 여기서 운동량을 주고받는다.
        //
        // 미는 일은 솔버 몫이었는데, 위 스윕이 판을 지운 자리에는 솔버가 잡을 접촉이
        // 안 생긴다. 그래서 운동량 교환이 거의 일어나지 않았다 - 증상은 "잔해가 안 밀리고
        // 배를 갉아 먹는다"다. Reaction의 주석이 "밀면 밀려나야지 부서지면 안 된다"고
        // 말하는데, 미는 코드가 없으면 그 문장이 성립할 수가 없었다.
        //
        // 규칙은 탄과 같은 것을 쓴다(<see cref="Ballistics.ImpactImpulse"/>).
        //
        // **양쪽 다 접촉점에 넣는다.** 예전에는 상대만 AddForceAtPosition이고 내 쪽은
        // `linearVelocity *= slowed / speed`였다 - 크기는 맞는데 **작용점이 없어서
        // 편심 충각이 나를 안 돌렸다.** 뱃머리 왼쪽 끝으로 들이받아도 상대만 돌고 나는
        // 반듯하게 느려지기만 했다. 회전을 만드는 코드가 따로 없는 것이 탄과 같은 이유다.
        Vector2 hit = Vector2.zero;

        if (joules > 0f && speed > 1e-3f)
        {
            // 위 회전 몫과 **같은 산수**의 선형 짝이다 - 내가 잃는 속도가 곧 상대가 받는
            // 운동량이라, 예산을 두 번 쪼개지 않고 한 곳에서 나눈다.
            float paid = joules * (linear / motion);

            float after = Mathf.Sqrt(
                Mathf.Max(0f, speed * speed - 2f * paid / Mathf.Max(1f, body.mass)));

            hit = Ballistics.ImpactImpulse(
                body.mass, velocity, velocity * (after / speed));
        }

        bool dynamicTarget = hitBody != null && hitBody.bodyType == RigidbodyType2D.Dynamic;

        // **완전비탄성 한계에서 자른다. 이걸 넘는 것이 곧 튕김이다.**
        //
        // 에너지에서 되돌린 값은 "판을 부수는 데 쓴 것"인데, 비탄성 충돌에서 그 에너지는
        // 변형으로 **소모되는** 것이지 상대를 미는 데 쓰이지 않는다. 그대로 충격량으로
        // 바꾸면 이중 계산이라, 264 대 1 질량비에서 잔해가 배보다 빠르게 튀어 나간다 -
        // 증상이 "잔해가 탱탱볼처럼 튄다"였다.
        //
        // 물리가 주는 상한은 둘이 같은 속도가 되는 지점이고, 그 충격량이
        // `환산질량 x 접근속도`다. 반발계수 0 - 이 게임에 튕기는 충돌은 없다.
        //
        // **자를 때는 양쪽을 같이 자른다.** 내 감속만 원값으로 내면 상대가 못 받은 몫이
        // 허공으로 사라지고, 나는 상대보다 느려져서 상대가 나를 관통해 지나간다.
        // 정적 콜라이더에는 안 자른다 - 벽은 얼마든지 받아낸다.
        if (dynamicTarget && hit.sqrMagnitude > 0f)
        {
            float approach = Vector2.Dot(velocity - hitBody.linearVelocity, dir);

            // 이미 멀어지는 중이면 밀 것이 없다. 안 보면 떠나는 잔해를 매 틱 걷어찬다.
            if (approach <= 0f)
                hit = Vector2.zero;
            else
            {
                float reduced = body.mass * hitBody.mass / (body.mass + hitBody.mass);
                float cap = reduced * approach;

                if (hit.magnitude > cap)
                    hit = hit.normalized * cap;
            }
        }

        // **밀어내는 몫 - 뉴턴 3법칙.** 엔진으로 밀면 상대가 밀리고 나는 그만큼 잃는다.
        // 이게 없으면 벽에 기대고 공짜로 가속하면서 상대를 영원히 갉는다.
        //
        // **전 추력이 접촉으로 다 넘어가지 않는다.** 붙어서 같이 밀려가는 두 몸은
        // `F / (m1 + m2)`로 **함께** 가속하고, 상대가 받는 힘은 그 질량 몫뿐이다. 통째로
        // 주면 8.4 MN짜리 구축함이 900 kg 운석을 틱당 155 m/s로 걷어찬다 - 증상이
        // "압력 피해 주면 겁나 빠르게 날아간다"였다. 그 몫이 정확히 Reaction이 재는
        // 값이라 새 식을 안 만든다.
        //
        // Ship.OnTick이 Ram()을 Drive()보다 먼저 돌리므로 이 반작용과 이번 틱 추력이 같은
        // Simulate에 들어가고, 둘을 합치면 양쪽 다 F/(m1+m2)로 간다 - 기대면 못 나간다.
        Vector2 push = Vector2.zero;

        if (dynamicTarget)
        {
            float pushForce = Mathf.Max(0f, Vector2.Dot(thrust, dir));

            if (pushForce > 0f)
                push = dir * (pushForce * dt * Reaction(body, hitBody));
        }

        Vector2 handOver = hit + push;

        if (handOver.sqrMagnitude > 1e-12f)
        {
            if (dynamicTarget)
                hitBody.AddForceAtPosition(handOver, where, ForceMode2D.Impulse);

            body.AddForceAtPosition(-handOver, where, ForceMode2D.Impulse);
        }

        // 작용 반작용. 상대가 먹은 만큼 내 뱃머리도 되받는다 - 유리를 받으면 안 긁히고,
        // 장갑을 받으면 뱃머리가 날아간다. 분기문 없이 상대의 강도가 내 피해를 정한다.
        // **물러나는 거리가 속도를 따라가야 한다.** where는 캐스트가 맞힌 상대 표면의
        // 점인데, 캐스트는 이번 틱 이동거리만큼 앞을 본다. 120 m/s면 접촉점이 내
        // 선체보다 2 m 앞이라, 고정 0.6 m를 물러나면 아직 허공이고 내 판이 안 잡힌다.
        //
        // 증상이 고약했다: 저속에서는 반작용이 멀쩡히 들어가는데 고속에서만 조용히
        // 사라져서, "빠르게 들이받으면 상대만 박살나고 나는 멀쩡하다"가 된다. 에러도
        // 로그도 없다 - if (bow != null)이 통째로 건너뛰기 때문이다.
        Armor bow = OwnPlateNear(root, where - dir * (reachedAt + 0.6f));

        if (bow == null)
        {
            if (RamLog)
                Debug.LogWarning(
                    $"[RAM] 반작용을 받을 내 판을 못 찾았다. 접촉 {reachedAt:F2} m 앞, "
                    + $"되받을 피해 {spent:F0}이 사라진다.", body);
        }
        else
        {
            bow.ApplyDamageEvenly(spent);
            DamageLog.Ram(bow);

            // Conduct가 번지는 이웃 판은 안 적는다 - 한 번 부딪히면 수십 장이 들어와서
            // 계기판이 통째로 주황이 된다. 접촉면만 적는 것이 "여기가 긁힌다"의 뜻이다.
            Conduct(bow, dir, spent,
                Ballistics.RamConductAlong, Ballistics.RamConductAcross,
                Ballistics.RamConductCutoff, Ballistics.RamConductMaxPlates);
        }

        if (target != null)
            Conduct(target, dir, spent,
                Ballistics.RamConductAlong, Ballistics.RamConductAcross,
                Ballistics.RamConductCutoff, Ballistics.RamConductMaxPlates);

    }

    /// <summary>
    /// 몸의 도달 반경 캐시. 값은 콜라이더마다 "피벗까지 거리 + 피벗에서 AABB 중심까지 +
    /// AABB 반대각"의 최대 - 진짜 도달거리의 **자세 불변 상한**이다. 세 항 모두 어느
    /// 자세에서 재도 상한이 유지된다: 피벗(콜라이더 transform)은 몸에 강체로 붙어 있어
    /// 중심거리가 불변이고, AABB 중심-피벗 거리는 콜라이더 offset의 크기라 불변이고,
    /// AABB 반대각은 정렬 상태가 최소다. **bounds.center-질량중심으로 재면 안 된다** -
    /// 포탑은 매 틱 자기 피벗 중심으로 도는데 offset이 있어서 bounds.center가 움직이고,
    /// 콜라이더 수는 그대로라 캐시가 상한 노릇을 못 하게 된다.
    ///
    /// 콜라이더 **수**가 변하면(판 사망·파단·잔해 입양) 다시 잰다. 수리는 HP만 돌리고
    /// 콜라이더를 안 만드니 수가 안 변하고, 그래서 캐시가 안 썩는다.
    /// </summary>
    private static readonly Dictionary<Rigidbody2D, (int count, float radius)> _reachCache = new();
    private static readonly List<Rigidbody2D> _pruneScratch = new();
    private static long _pruneTick = -1;

    /// <summary>
    /// 죽은(Destroy된) Object 키만 걷어낸다. <see cref="_reachCache"/>와 <see cref="_colCache"/>
    /// 둘 다 같은 모양(넘칠 때만, 틱당 1회, 죽은 키만)이라 여기 하나로 합친다.
    /// </summary>
    private static void PruneDeadKeys<TKey, TValue>(
        Dictionary<TKey, TValue> cache, List<TKey> scratch, int threshold, ref long lastPruneTick)
        where TKey : UnityEngine.Object
    {
        if (cache.Count <= threshold || lastPruneTick == Core.TickManager.currentTick)
            return;

        lastPruneTick = Core.TickManager.currentTick;
        scratch.Clear();

        foreach (KeyValuePair<TKey, TValue> pair in cache)
        {
            if (pair.Key == null)
                scratch.Add(pair.Key);
        }

        for (int i = 0; i < scratch.Count; i++)
            cache.Remove(scratch[i]);
    }

    private static float CachedRadius(Rigidbody2D body)
    {
        int count = body.attachedColliderCount;

        if (_reachCache.TryGetValue(body, out (int count, float radius) hit))
        {
            if (hit.count == count)
                return hit.radius;

            // 콜라이더가 **줄었으면** 재측정하지 않는다. 판 사망·파단은 반경을 늘리지
            // 못하므로 기존 값이 여전히 유효한 상한이고, 그라인딩 중에는 거의 매 틱
            // 판이 죽어서 여기서 재측정하면 캐시가 캐시 노릇을 못 한다. 실제 판정은
            // 접점별 점속도 필터가 하니 헐거운 상한은 스윕만 약간 길게 할 뿐이다.
            if (hit.count > count)
            {
                _reachCache[body] = (count, hit.radius);
                return hit.radius;
            }
        }

        // 죽은 몸의 항목은 넘칠 때만, 죽은 키만 걷어낸다. 통째로 Clear하면 잔해가 512개를
        // 넘는 구름(정확히 이 캐시가 겨냥한 장면)에서 미스마다 전원 재측정하는 스래싱이 된다.
        // 걷어내기는 틱당 1회 - 산 몸이 진짜로 상한을 넘으면 사전이 자라게 두는 쪽이 싸다.
        PruneDeadKeys(_reachCache, _pruneScratch, 1024, ref _pruneTick);

        Vector2 centre = body.worldCenterOfMass;
        int n = body.GetAttachedColliders(_attached);
        float best = 0f;

        for (int i = 0; i < n; i++)
        {
            Collider2D c = _attached[i];

            if (c == null || !c.enabled)
                continue;

            Bounds b = c.bounds;
            Vector2 pivot = c.transform.position;

            float r = (pivot - centre).magnitude
                + ((Vector2)b.center - pivot).magnitude
                + ((Vector2)b.extents).magnitude;

            if (r > best)
                best = r;
        }

        _reachCache[body] = (count, best);
        return best;
    }

    /// <summary>
    /// 이번 틱의 (몸, 중심, 반경) 스냅샷. Punch는 몸마다 매 틱 도는데, 각자
    /// HullStructure.All 전체에 worldCenterOfMass·CachedRadius(네이티브)를 물으면
    /// 잔해 N개 구름에서 O(N²) 네이티브 호출이 된다 - 그라인딩 지속 렉의 최대 단일 원인.
    /// 틱 안에서는 Simulate가 한 번뿐이라 위치가 안 변하므로 첫 호출자가 지은 것을
    /// 전원이 재사용해도 결과가 같다. 같은 틱에 태어난 잔해가 다음 틱까지 안 보이는
    /// 창이 생기지만, 그 창은 지금도 OnTick 순회 순서로 이미 존재한다.
    /// </summary>
    private static readonly List<(Rigidbody2D body, Vector2 centre, float radius, Vector2 velocity)> _bodySnapshot = new();
    private static long _snapshotTick = -1;

    private static void RefreshBodySnapshot()
    {
        if (_snapshotTick == Core.TickManager.currentTick)
            return;

        _snapshotTick = Core.TickManager.currentTick;
        _bodySnapshot.Clear();

        List<HullStructure> all = HullStructure.All;

        for (int i = 0; i < all.Count; i++)
        {
            HullStructure other = all[i];

            if (other == null)
                continue;

            Rigidbody2D otherBody = other.Body;

            if (otherBody == null)
                continue;

            // **속도도 여기서 캐시한다.** 아래 GatherNearBodies가 몸마다 상대속도를 보는데,
            // 그 함수는 몸마다 불리므로 거기서 linearVelocity를 읽으면 네이티브 접근이
            // O(n²)가 된다 - 운석 90개면 틱당 8,100번이다. 스냅샷은 틱당 한 번이라 O(n)이다.
            _bodySnapshot.Add((otherBody, otherBody.worldCenterOfMass, CachedRadius(otherBody), otherBody.linearVelocity));
        }
    }

    /// <summary>
    /// 내 사거리 + 상대 반경 안의 남의 몸을 모아 둔다. Cast의 브로드페이즈이자,
    /// <see cref="SweepNearColliders"/>가 콜라이더를 고르는 기준이다. 비었으면 false.
    /// </summary>
    private static readonly List<(Vector2 centre, float radius)> _nearBodies = new();

    private static bool GatherNearBodies(
        Rigidbody2D self, Vector2 centre, float range,
        Vector2 selfVelocity, float spinReach, bool pushing)
    {
        using var _ = _mGather.Auto();

        RefreshBodySnapshot();

        _nearBodies.Clear();

        for (int i = 0; i < _bodySnapshot.Count; i++)
        {
            (Rigidbody2D otherBody, Vector2 at, float radius, Vector2 otherVelocity)
                = _bodySnapshot[i];

            if (otherBody == self)
                continue;

            float r = range + radius;

            if ((at - centre).sqrMagnitude > r * r)
                continue;

            // **상대 운동으로 거른다.** 에너지를 상대속도로 재게 바꿔 놓고 게이트만
            // 절대속도로 두면, 배에 실려 다니는 잔해가 아래 SweepNearColliders(판마다
            // Cast를 도는 진짜 비싼 자리)를 매 틱 전부 돌고 나서 피해 0을 내고 끝난다 -
            // 피해는 멎었는데 비용은 그대로인 최악의 조합이다. 여기서 끊으면 그 몸은
            // Cast를 한 번도 안 돈다.
            //
            // 밀고 있으면(pushing) 상대속도가 0이어도 통과시킨다 - 기대서 찌그러뜨리는
            // 것이 압착이고, 그건 상대 운동이 없을 때 하는 일이다.
            //
            // sqrt를 안 쓴다. `|dv| + spinReach < RamMinSpeed`를 `|dv| < 남은 몫`으로
            // 옮기면 제곱 비교로 끝난다 - 이 줄은 몸마다 x 몸마다라 O(n^2)다.
            if (!pushing && spinReach < Ballistics.RamMinSpeed)
            {
                float slack = Ballistics.RamMinSpeed - spinReach;

                if ((selfVelocity - otherVelocity).sqrMagnitude < slack * slack)
                    continue;
            }

            _nearBodies.Add((at, radius));
        }

        return _nearBodies.Count > 0;
    }

    private static readonly RaycastHit2D[] _castHits = new RaycastHit2D[128];

    private sealed class HitDistance : System.Collections.Generic.IComparer<RaycastHit2D>
    {
        public int Compare(RaycastHit2D a, RaycastHit2D b) => a.distance.CompareTo(b.distance);
    }

    private static readonly HitDistance _byDistance = new();

    /// <summary>
    /// 콜라이더의 몸 로컬 피벗 + 자세 불변 도달 반경. 콜라이더는 몸 안에서 강체로 붙어
    /// 있어서(포탑도 피벗은 고정, 회전만 한다) 한 번 재면 영원히 맞다 - 선별 루프가
    /// 콜라이더마다 bounds(네이티브)를 읽던 것을 순수 산술로 바꾼다.
    /// 반경은 CachedRadius와 같은 상한 공식: |AABB중심-피벗| + 반대각.
    /// </summary>
    private static readonly Dictionary<Collider2D, (Vector2 pivotLocal, float reach)> _colCache = new();
    private static readonly List<Collider2D> _colPrune = new();
    private static long _colPruneTick = -1;

    private static (Vector2 pivotLocal, float reach) ColliderLocal(Transform bodyT, Collider2D c)
    {
        if (_colCache.TryGetValue(c, out (Vector2 pivotLocal, float reach) hit))
            return hit;

        PruneDeadKeys(_colCache, _colPrune, 4096, ref _colPruneTick);

        Vector2 pivot = c.transform.position;
        Bounds b = c.bounds;

        var entry = (
            (Vector2)bodyT.InverseTransformPoint(pivot),
            ((Vector2)b.center - pivot).magnitude + ((Vector2)b.extents).magnitude);

        _colCache[c] = entry;
        return entry;
    }

    private static readonly List<Vector2> _nearLocal = new();

    /// <summary>
    /// 반경 <paramref name="radius"/>짜리 원을 <paramref name="from"/>에서 <paramref name="dir"/>
    /// 방향으로 <paramref name="step"/>만큼 밀었을 때 <paramref name="target"/>을 스칠 수 있나.
    ///
    /// **원 검사를 캡슐로 좁히는 것이 요점이다.** 예전에는 `거리 <= reach + step`이라
    /// 뒤·옆에 있는 콜라이더까지 후보가 됐다 - 이번 틱에 절대 안 닿는 자리인데도 비싼
    /// Cast를 한 번씩 냈다.
    ///
    /// **보수적으로만 틀려야 한다.** true를 잘못 내면 헛Cast 한 번이고, false를 잘못 내면
    /// 충각이 통째로 사라진다(시뮬 버그). 그래서 시작점 **뒤로도** radius만큼은 남긴다 -
    /// 이미 겹쳐 있는 접촉이 그 자리다.
    /// </summary>
    private static bool SweptCircleMayHit(
        Vector2 from, Vector2 target, Vector2 dir, float step, float radius)
    {
        Vector2 rel = target - from;
        float along = Vector2.Dot(rel, dir);

        if (along < -radius || along > step + radius)
            return false;

        float sideSq = rel.sqrMagnitude - along * along;

        return sideSq <= radius * radius;
    }

    /// <summary>
    /// 남의 몸 근처에 있는 콜라이더만 골라 스윕한다. 판 300장짜리 배가 갈고 있어도
    /// 실제로 캐스트되는 건 접촉면의 몇십 장이다. 결과는 거리순 - body.Cast가 주던
    /// 순서를 정렬로 복원한다.
    /// </summary>
    /// <param name="swing">
    /// 이번 틱에 회전으로 쓸리는 거리(m). **capsule을 안전하게 만드는 항이다** - step에는
    /// 회전 몫이 이미 들어 있는데 capsule은 직선 dir 하나로 자르므로, 제자리 회전으로
    /// 옆구리를 후려치는 판이 통째로 빠진다. 이만큼 반경을 부풀리면 병진만 있을 때는 0이라
    /// 순수 capsule이고, 회전이 지배하면 원으로 되돌아간다 - 그때는 실제로 전방향이다.
    /// </param>
#if UNITY_EDITOR
    /// <summary>
    /// capsule 검사는 **보수적으로만 틀려야 한다.** true를 잘못 내면 헛Cast 한 번이지만,
    /// false를 잘못 내면 그 틱의 충각이 통째로 사라진다 - 링 검사(RemovalMightSplit)와
    /// 같은 비대칭이라 같은 방식으로 못 박는다.
    /// </summary>
    internal static bool SweptCircleSelfTest()
    {
        Vector2 from = Vector2.zero;
        Vector2 dir = Vector2.right;

        // 진행 방향 정면, 사거리 안 - 반드시 잡는다.
        bool ahead = SweptCircleMayHit(from, new Vector2(5f, 0f), dir, 10f, 1f);

        // 바로 뒤 - 예전 원 검사는 잡았고 capsule은 버린다. 그것이 이 최적화의 전부다.
        bool behind = SweptCircleMayHit(from, new Vector2(-5f, 0f), dir, 10f, 1f);

        // 이미 겹쳐 있는 접촉은 시작점보다 뒤에 있어도 살아야 한다.
        bool touching = SweptCircleMayHit(from, new Vector2(-0.5f, 0f), dir, 10f, 1f);

        // 옆으로 반경 밖 - 아무리 멀리 가도 안 스친다.
        bool aside = SweptCircleMayHit(from, new Vector2(5f, 3f), dir, 10f, 1f);

        // 옆이지만 반경 안 - 스친다.
        bool grazing = SweptCircleMayHit(from, new Vector2(5f, 0.9f), dir, 10f, 1f);

        // 사거리 너머 - 이번 틱에는 못 닿는다.
        bool far = SweptCircleMayHit(from, new Vector2(20f, 0f), dir, 10f, 1f);

        // 반경을 그만큼 부풀리면(= swing) 뒤쪽도 도로 들어온다. 제자리 회전이 그 경우다.
        bool swung = SweptCircleMayHit(from, new Vector2(-5f, 0f), dir, 10f, 6f);

        return ahead && !behind && touching && !aside && grazing && !far && swung;
    }
#endif

    private static int SweepNearColliders(Rigidbody2D body, Vector2 dir, float step, float swing)
    {
        using var _ = _mSweep.Auto();
        int attached = body.GetAttachedColliders(_attached);
        int n = 0;

        // 남의 몸 중심을 내 몸 로컬로 한 번만 옮긴다(몸 몇 개 = 네이티브 몇 번).
        // 그 뒤로 콜라이더 선별 루프는 캐시된 로컬 피벗과의 float 비교뿐이다 -
        // 콜라이더 300개 x bounds 네이티브가 여기서 사라졌다.
        Transform bodyT = body.transform;
        _nearLocal.Clear();

        for (int k = 0; k < _nearBodies.Count; k++)
            _nearLocal.Add(bodyT.InverseTransformPoint(_nearBodies[k].centre));

        // **부호를 손으로 마저 뒤집는다.** InverseTransformDirection은 scale을 무시하는데
        // 위의 InverseTransformPoint는 안 무시한다 - 반전 함선(localScale.x = -1)에서 둘을
        // 그냥 섞으면 방향만 거울이 아니라서 capsule이 엉뚱한 쪽을 본다. Conduct의 axisL과
        // 같은 자리다.
        Vector2 dirLocal = bodyT.InverseTransformDirection(dir);
        Vector3 ls = bodyT.lossyScale;
        dirLocal = new Vector2(dirLocal.x * Mathf.Sign(ls.x), dirLocal.y * Mathf.Sign(ls.y));

        for (int i = 0; i < attached; i++)
        {
            Collider2D c = _attached[i];

            if (c == null || !c.enabled)
                continue;

            (Vector2 pivotLocal, float reach) col = ColliderLocal(bodyT, c);
            float mine = col.reach + swing;
            bool near = false;

            for (int k = 0; k < _nearBodies.Count; k++)
            {
                float r = _nearBodies[k].radius + mine;

                if (SweptCircleMayHit(col.pivotLocal, _nearLocal[k], dirLocal, step, r))
                {
                    near = true;
                    break;
                }
            }

            if (!near)
                continue;

            int hits = c.Cast(dir, _castHits, step, ignoreSiblingColliders: true);

            for (int h = 0; h < hits && n < _punch.Length; h++)
                _punch[n++] = _castHits[h];
        }

        if (n > 1)
            System.Array.Sort(_punch, 0, n, _byDistance);

        return n;
    }

    /// <summary>
    /// 중심에서 이 몸의 제일 먼 점까지의 거리. 회전이 한 틱에 닿을 수 있는 범위를 정한다.
    ///
    /// 콜라이더 bounds의 네 모서리를 본다. AABB라 회전한 판에서는 살짝 크게 나오는데,
    /// 크게 나오는 쪽이 안전하다 - 이 값은 스윕 **상한**일 뿐이고, 실제로 어느 판이 닿는지는
    /// 점마다 v + ω x r로 다시 거른다.
    /// </summary>
    private static float FarthestReach(Rigidbody2D body, Vector2 centre, out Vector2 farPoint)
    {
        farPoint = centre;

        int n = body.GetAttachedColliders(_attached);
        float best = 0f;

        for (int i = 0; i < n; i++)
        {
            if (_attached[i] == null || !_attached[i].enabled)
                continue;

            Bounds b = _attached[i].bounds;

            for (int corner = 0; corner < 4; corner++)
            {
                var p = new Vector2(
                    (corner & 1) == 0 ? b.min.x : b.max.x,
                    (corner & 2) == 0 ? b.min.y : b.max.y);

                float d = (p - centre).sqrMagnitude;

                if (d <= best)
                    continue;

                best = d;
                farPoint = p;
            }
        }

        return Mathf.Sqrt(best);
    }

    /// <summary>
    /// 미는 힘 중 이 판에게 실제로 전달되는 몫(0~1).
    ///
    /// **자유롭게 떠 있는 것은 밀어서 못 부순다.** 두 몸이 접촉한 채로 밀리면 둘 다
    /// `a = F / (m_self + m_other)`로 **같이** 가속한다 - 상대가 그대로 따라오므로 응력이
    /// 안 쌓인다. 접촉이 상대에게 주는 힘은 `m_other x a`이고, 그래서 비율이
    /// `m_other / (m_self + m_other)`가 된다. 솔버가 쓰는 환산질량과 같은 셈이다.
    ///
    /// 200 kg 파편이면 0.002다. 사실상 면제이고, 그게 맞다 - 밀면 밀려나야지 부서지면 안 된다.
    /// 반대로 안 밀리는 것(키네마틱·정적, 거울 껍질이 그 경우)은 1을 받아 온전히 눌린다.
    /// </summary>
    private static float Reaction(Rigidbody2D body, Rigidbody2D other)
    {
        // 정적 콜라이더는 리지드바디가 없다. 안 밀리므로 온전히 눌린다.
        if (other == null || other == body)
            return 1f;

        if (other.bodyType != RigidbodyType2D.Dynamic)
            return 1f;

        return other.mass / Mathf.Max(1f, other.mass + body.mass);
    }

    /// <summary>
    /// 접점 바로 뒤에 있는 내 판. 반작용을 받을 뱃머리다. Rigidbody2D.Cast는 무엇에 닿았는지만
    /// 알려주고 **내 어느 콜라이더가 닿았는지는 안 알려주므로** 여기서 한 번 되짚는다.
    /// </summary>
    private static Armor OwnPlateNear(Transform root, Vector2 at)
    {
        int n = Physics2D.OverlapCircleNonAlloc(at, 0.7f, _nearby);

        for (int i = 0; i < n; i++)
        {
            Collider2D col = _nearby[i];

            if (col != null && col.transform.IsChildOf(root)
                && col.TryGetComponent(out Armor plate) && plate != null)
                return plate;
        }

        return null;
    }

    /// <summary>
    /// 충격이 선체 구조를 타고 번진다. 포탄과 충각의 차이가 이것이다 - 포탄은 한 점을 뚫고
    /// 지나가고, 충각은 배를 굽힌다.
    ///
    /// **번지는 모양이 등방성이 아니다.** 충격축을 따라서는 거의 안 줄고, 옆으로는 급히 죽는다.
    /// 그래서 진입 지점에서 반대편 외판까지 폭 한두 칸짜리 띠가 통째로 상한다 - 배를 굽히면
    /// 그 단면 전체가 견디는 것이지 맞은 자리만 견디는 게 아니기 때문이다.
    ///
    /// **허리를 끊는 코드는 여기 없다.** 띠가 외판까지 이어지면 그 판들이 죽고, 다음 틱에
    /// HullStructure의 8방향 BFS가 두 덩어리를 찾아 알아서 떼어낸다. 원래 있던 길이다.
    ///
    /// BFS와 감쇠 공식이 하는 일이 다르다: BFS는 **어디까지 닿는가**(실물로 이어져 있어야
    /// 충격이 간다. 이미 뚫린 구멍 너머로는 안 넘어간다), 공식은 **얼마나 먹는가**. 감쇠를
    /// 경로에 누적하지 않고 위치에서 바로 구하므로, 어느 순서로 도달하든 같은 값이 나온다.
    /// </summary>
    /// <param name="axis">접촉면 법선. 부호는 안 쓴다 - 축의 양쪽으로 똑같이 번진다.</param>
    /// <param name="along">축을 1 m 따라갈 때 남는 몫.</param>
    /// <param name="across">축에서 1 m 벗어날 때 남는 몫. along과 같으면 등방성 = 폭발.</param>
    private static void Conduct(
        Armor origin, Vector2 axis, float damage,
        float along, float across, float cutoff01, int maxPlates)
    {
        // **재진입한다.** 판이 죽으면 Armor.Die가 그 자리에서 CollapseRemains로 파편을 뿜고,
        // 그 파편이 다른 탄약고를 맞히면 CriticalModule.TakeDamage -> Detonate -> 여기까지
        // 전부 **동기로** 돌아온다. 정적 버퍼를 나눠 쓰면 안쪽 폭발이 _wave를 비우고, 바깥
        // while이 빈 큐를 보고 조용히 끝난다 - 제일 큰 폭발이 제일 적게 번지는 증상이다.
        //
        // MaxDetonationChain은 깊이만 막지 이 공유 상태는 못 막는다. 흔한 길이 아니므로
        // 재진입일 때만 할당한다 - 평시 경로는 예전 그대로 무할당이다.
        using var _ = _mConduct.Auto();

        bool nested = _conducting > 0;
        Armor[] wave = nested ? NestedWave(_conducting, maxPlates + 8) : _wave;
        HashSet<Armor> reached = nested ? NestedReached(_conducting) : null;
        int head = 0, tail = 0, reachedCount = 0;

        // 이번 파면의 도장 번호. 안 겹치면 지울 일이 없다.
        int stamp = ++_conductStamp;

        wave[tail++] = origin;
        reachedCount++;

        if (nested)
            reached.Add(origin);
        else
            origin.ConductStamp = stamp;

        // **간선 계산을 전부 배 로컬로.** 판의 CellLocal은 캐시(재부모화에도 불변)라
        // 간선마다 나가던 transform.position 네이티브 호출이 0이 된다. 축만 한 번
        // 로컬로 돌린다 - InverseTransformDirection은 scale을 무시하므로 반전 함선
        // (localScale.x = -1)의 부호를 손으로 마저 적용해야 한다. 안 하면 거울상 배에서
        // 감쇠 띠가 거울상이 아니라 엉뚱한 축으로 돈다.
        Transform bodyT = origin.CachedBody;
        Vector2 axisL = axis;

        if (bodyT != null)
        {
            axisL = bodyT.InverseTransformDirection(axis);
            Vector3 ls = bodyT.lossyScale;
            axisL = new Vector2(axisL.x * Mathf.Sign(ls.x), axisL.y * Mathf.Sign(ls.y));
        }

        Vector2 acrossAxis = new(-axisL.y, axisL.x);
        Vector2 pivot = origin.CellLocal;
        // 컷오프를 지수 쪽으로 옮겨 둔 것. 루프에서 Exp를 돌리기 **전에** 이 값과 비교한다.
        float lnCutoff = Mathf.Log(Mathf.Max(1e-6f, cutoff01));

        // Pow 두 번을 Exp 한 번으로: a^x * b^y = exp(x ln a + y ln b). 감쇠 공식 결과는 동일.
        float lnAlong = Mathf.Log(Mathf.Max(1e-6f, along));
        float lnAcross = Mathf.Log(Mathf.Max(1e-6f, across));

        _conducting++;

        try
        {
            while (head < tail && reachedCount < maxPlates)
            {
                Armor at = wave[head++];

                if (at == null)
                    continue;

                foreach (Armor neighbour in at.Neighbours)
                {
                    // == null: 이미 부서진 판. 부서진 자리로는 충격이 안 지나간다.
                    // SameBodyAs: 잔해로 갈라진 조각. 참조는 살아 있어도 이제 남의 몸이다.
                    if (neighbour == null || !at.SameBodyAs(neighbour))
                        continue;

                    if (nested ? reached.Contains(neighbour) : neighbour.ConductStamp == stamp)
                        continue;

                    Vector2 offset = neighbour.CellLocal - pivot;

                    // 칸이 1 m라 거리가 그대로 미터다. Abs인 이유: Unity의 접촉면 법선 부호는
                    // 콜백을 받는 쪽에 따라 뒤집힌다. 어차피 축의 양쪽으로 똑같이 번지면 된다.
                    float exponent =
                        Mathf.Abs(Vector2.Dot(offset, axisL)) * lnAlong
                        + Mathf.Abs(Vector2.Dot(offset, acrossAxis)) * lnAcross;

                    // **컷오프를 지수에서 본다.** damage > 0이므로
                    //   damage*exp(e) < damage*cutoff01  <=>  e < ln(cutoff01)
                    // 이라 결과가 글자 그대로 같고, 버릴 판에는 Exp 자체를 안 돈다.
                    // 유폭 한 번이 BlastMaxPlates(96)장을 도는데 대부분은 여기서 걸린다 -
                    // 이 BFS는 끝을 확인하려고 항상 경계 밖까지 한 겹 더 본다.
                    if (exponent < lnCutoff)
                        continue;

                    float share = damage * Mathf.Exp(exponent);

                    if (nested)
                        reached.Add(neighbour);
                    else
                        neighbour.ConductStamp = stamp;

                    reachedCount++;

                    // 파면은 판마다 한 번씩만 들어오지만, 상한을 넘어서까지 자라지는
                    // 않게 정적 버퍼는 넉넉히 키운다.
                    if (tail == wave.Length)
                    {
                        System.Array.Resize(ref wave, wave.Length * 2);

                        if (!nested)
                            _wave = wave;
                    }

                    wave[tail++] = neighbour;
                    neighbour.ApplyDamageEvenly(share, Ballistics.ConductHeatScale);
                }
            }
        }
        finally
        {
            _conducting--;
        }
    }

    /// <summary>
    /// 유폭. 충각과 같은 전도인데 등방성이다 - along과 across가 같으면 띠가 아니라 원이 된다.
    /// 그래서 "폭발"이라는 별도 시스템이 없다. 충각은 배를 굽혀 자르고, 폭발은 둥글게 판다.
    ///
    /// 어디까지 닿는지는 BlastCutoff가 정하고 damage는 "닿은 판이 죽느냐"만 정한다. destroyer로
    /// 재보면 damage 800이면 판 17장 구멍, 1600이면 선체가 갈라진다.
    ///
    /// **매질이 둘이다.** 구조를 타고 가는 것(Conduct)과 빈 공간을 건너가는 것(Radiate).
    /// 앞엣것은 이어진 실물만 따라가므로 이미 뚫린 구멍에서 끊기고, 뒤엣것은 몸을 안 가리므로
    /// 맞댄 적함·잔해·운석에도 닿는다. 감쇠 공식은 같은 것을 쓴다.
    /// </summary>
    public static void Detonate(Armor origin, float damage)
    {
        // 한 폭발이 구조 전도와 자유 공간에 낳는 파편도 같은 순간의 한 wave다.
        using var spallBatch = SpallResolver.DeferPump();

        origin.ApplyDamageEvenly(damage);

        Conduct(origin, Vector2.up, damage,
            Ballistics.BlastFalloff, Ballistics.BlastFalloff,
            Ballistics.BlastCutoff, Ballistics.BlastMaxPlates);

        Radiate(origin, Mathf.Sqrt(damage));

        // 반대편 벽도 같이 맞는다. Conduct는 판 그래프를, Radiate는 콜라이더를 타는데
        // 후면은 둘 다에 없어서 세 번째 문이 필요하다 - 콜라이더를 달아 해결하면 후면이
        // 오브젝트가 되고 "함선에는 Z축이 없다"가 깨진다.
        HullStructure.BlastRear(origin.transform.position, damage);
    }

    /// <summary>
    /// 반경 안의 판을 몸에 상관없이 때린다. 폭심에서의 거리 하나로 정해지므로 Conduct와
    /// 완전히 같은 감쇠를 쓴다 - 다른 것은 "이어져 있어야 간다"를 안 본다는 것뿐이다.
    ///
    /// **여기가 유폭이 배를 건너가는 유일한 자리다.** <see cref="Armor.Neighbours"/>는
    /// 자기 배의 격자에서 채워지므로 적함 판이 애초에 들어 있지 않고, SameBodyAs를 빼도
    /// 갈라져 나간 잔해 참조만 되살아난다. 그래서 그래프로는 못 하고 질의가 필요하다.
    ///
    /// CLAUDE.md가 막은 OverlapCircle과 다른 물건이다. 그쪽은 판마다·틱마다 돌면서 반경
    /// 때문에 두 칸 건너를 집고 남의 배까지 집어오는 것이 문제였다. 여기서는 유폭 한 번에
    /// 한 번이고, 반경이 곧 정의이며, 남의 배를 집는 것이 목적이다.
    ///
    /// **자기 몸은 건드리지 않는다.** 그쪽은 Conduct의 몫이라 겹치면 두 번 먹는다.
    /// 정적 _reached로 거르지 않는 이유는 그게 재진입에 안 버티기 때문이다 - 연쇄 유폭이
    /// 동기로 돌아 그 집합을 갈아치우면, 바깥 폭발이 자기 판을 자유 공간 몫으로 한 번 더
    /// 때린다. 부모 비교는 공유 상태를 안 읽으므로 그 창이 존재하지 않는다.
    ///
    /// 그래서 매질이 몸 경계로 정확히 갈린다: 자기 몸은 구조를 타고(구멍에서 끊기고),
    /// 남의 몸은 빈 공간을 건너간다(거리만 본다).
    ///
    /// ponytail: 시야 차폐를 안 본다. 반경이 7 m라 구축함 반대편은 이미 컷오프 밖이고,
    /// 맞댄 두 배 사이에는 가릴 것이 없다. 큰 배가 생겨서 관통선이 문제가 되면 폭심에서
    /// 판으로 Linecast 한 발이 승급 경로다.
    /// </summary>
    private static void Radiate(Armor origin, float damage)
    {
        // Conduct와 같은 재진입 방어. 아래 ApplyDamageEvenly가 다른 탄약고를 터뜨리면
        // 안쪽 Radiate가 같은 버퍼에 질의를 다시 써서, 바깥 루프가 읽던 목록이 통째로 바뀐다.
        bool nested = _radiating > 0;
        Collider2D[] hits = nested ? NestedNearby(_radiating) : _nearby;

        Vector2 pivot = origin.transform.position;
        float cutoff = damage * Ballistics.BlastCutoff;

        int n = Physics2D.OverlapCircleNonAlloc(pivot, Ballistics.BlastRadius, hits);

        // 조용히 잘리면 "다 닿았다"로 읽힌다. 버퍼가 꽉 찼다는 건 반경 안에 판이 아닌
        // 콜라이더가 잔뜩 있다는 뜻이므로, 그때는 레이어 마스크를 붙여야 한다.
        if (n == hits.Length)
            Debug.LogWarning($"[RamImpact] 유폭 질의 버퍼 {n}개가 꽉 찼다. 일부 판을 놓쳤다.");

        _radiating++;

        try
        {
        for (int i = 0; i < n; i++)
        {
            Collider2D col = hits[i];

            // 이번 틱에 이미 죽은 판. Destroy는 프레임 끝까지 콜라이더를 남겨 두지만
            // == null은 즉시 참이 된다.
            if (col == null || !col.TryGetComponent(out Armor plate) || plate == null)
                continue;

            // 자기 몸은 Conduct가 이미 맡았다. 폭심 자신도 여기서 걸린다.
            if (plate.SameBodyAs(origin))
                continue;

            float share = damage * Mathf.Pow(
                Ballistics.BlastFalloff, Vector2.Distance(plate.transform.position, pivot));

            if (share < cutoff)
                continue;

            plate.ApplyDamageEvenly(share);
        }
        }
        finally
        {
            _radiating--;
        }
    }

}
