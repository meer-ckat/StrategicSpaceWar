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

    private static readonly HashSet<Armor> _seen = new();

    /// <summary>
    /// <see cref="FarthestReach"/>가 이 몸의 콜라이더를 받아 오는 자리. 함선 한 척이 판
    /// 300장이고 모듈까지 붙으므로 넉넉히 잡는다. Punch가 OnTick에서만 불리고 재진입하지
    /// 않으므로 정적이어도 안전하다 - Conduct/Radiate와 달리 이 안에서 피해가 안 나간다.
    /// </summary>
    private static readonly Collider2D[] _attached = new Collider2D[1024];

    // 접점마다 새로 만들면 한 번 부딪힐 때 최대 16쌍이 쓰레기가 된다. 충각은 난전에서
    // 매 틱 들어온다.
    private static readonly Queue<Armor> _wave = new();
    private static readonly HashSet<Armor> _reached = new();

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
        Vector2 centre = body.worldCenterOfMass;
        float rMax = FarthestReach(body, centre, out Vector2 farPoint);

        // 이번 틱에 이 몸의 **어느 점이든** 나아갈 수 있는 최대 거리. 스윕 길이의 상한이다.
        float reach = speed + Mathf.Abs(omega) * rMax;

        if (reach < Ballistics.RamMinSpeed && !pushing)
            return;

        // 서 있으면 진행 방향이 없다. 도는 중이면 제일 먼 점의 접선이 곧 휘두르는 쪽이고,
        // 그것도 없으면 밀고 있는 쪽이 파고드는 쪽이다.
        Vector2 dir;

        if (speed > 1e-3f)
            dir = velocity / speed;
        else if (Mathf.Abs(omega) * rMax > 1e-3f)
            dir = (Ballistics.Rotate(farPoint - centre, 90f) * Mathf.Sign(omega)).normalized;
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

        // **솔버보다 한 틱 앞서 본다.** 딱 이번 틱 이동거리만 쓸면 판을 지우기 시작하는
        // 그 틱에 솔버도 접촉을 잡아서, 얇은 것이 여러 개 겹친 자리(거울 잔해 구름)에서는
        // 다 못 치운 나머지가 동시 접촉으로 배를 튕겨낸다.
        float lead = dt * Ballistics.RamLookahead;
        float step = reach * lead + Ballistics.RamSkin;
        int n = body.Cast(dir, _punch, step);

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
        _seen.Clear();

        Vector2 where = body.worldCenterOfMass;

        // 거리순으로 오므로 앞에서부터 담긴다. 중복은 첫 번째(제일 가까운) 것만 남는다.
        for (int i = 0; i < n; i++)
        {
            Collider2D probe = _punch[i].collider;

            if (probe == null || !probe.TryGetComponent(out Armor plate) || plate == null)
                continue;

            if (!_seen.Add(plate))
                continue;

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
                where = _punch[i].point;

            _plates.Add(plate);
            _plateBodies.Add(_punch[i].rigidbody);
        }

        int contacts = _plates.Count;

        if (contacts == 0)
            return;

        // **회전 운동에너지도 예산이다.** 여기서만 진짜 관성 모멘트를 쓴다 - Ship.Angle은
        // 각속도를 직접 대입하고 관성 모멘트를 angleAccel에 녹여 두었지만, 그건 조종 모델의
        // 사정이고 에너지는 리지드바디가 콜라이더에서 뽑아 둔 body.inertia가 진실이다.
        float linear = 0.5f * body.mass * speed * speed;
        float spin = 0.5f * body.inertia * omega * omega;
        float motion = linear + spin;

        float press = Mathf.Max(0f, Vector2.Dot(thrust, dir)) * step;

        float pressEach = press * scale / contacts;

        // 한 틱에 쏟을 수 있는 몫만 들고 들어간다. 예산 전부를 한 틱에 태우면 못 뚫는 벽에서
        // 속도가 0으로 떨어지고, 그러면 솔버가 접촉도 회전도 못 만든다.
        float pool = motion * scale * Ballistics.RamSpendPerTick;
        float left = pool;

        Armor target = null;

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
            float share = pressEach * react;

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

            target ??= plate;
        }

        float fromMotion = pool - left;
        float spent = fromMotion + press * scale;

        if (RamLog)
            Debug.Log(
                $"[RAM] v={speed:F1} w={body.angularVelocity:F1}도/s rMax={rMax:F1} "
                + $"reach={reach:F1} 접촉={contacts}장 충돌풀={pool:F0}(쓴 {fromMotion:F0}, "
                + $"선형 {linear / motion:P0}) 압력={press * scale:F1}(판당 {pressEach:F2}) "
                + $"step={step:F2}", body);

        if (spent <= 0f)
            return;

        // 작용 반작용. 상대가 먹은 만큼 내 뱃머리도 되받는다 - 유리를 받으면 안 긁히고,
        // 장갑을 받으면 뱃머리가 날아간다. 분기문 없이 상대의 강도가 내 피해를 정한다.
        Armor bow = OwnPlateNear(root, where - dir * 0.6f);

        if (bow != null)
        {
            bow.ApplyDamageEvenly(spent);
            Conduct(bow, dir, spent,
                Ballistics.RamConductAlong, Ballistics.RamConductAcross,
                Ballistics.RamConductCutoff, Ballistics.RamConductMaxPlates);
        }

        if (target != null)
            Conduct(target, dir, spent,
                Ballistics.RamConductAlong, Ballistics.RamConductAcross,
                Ballistics.RamConductCutoff, Ballistics.RamConductMaxPlates);

        // 쓴 만큼 느려진다. 되돌리는 코드가 없다 - 솔버가 이 판들을 볼 일이 아예 없으므로
        // 되돌릴 것도 없고, 그래서 꺾임 프레임도 없다.
        //
        // **속도를 세팅하지 않고 비율로 줄인다.** `= dir * slowed`로 덮으면 두 가지가 틀린다:
        // 옆으로 흐르던 속도가 사라지고, 못 뚫을 때 우리가 배를 먼저 세워버려서 **솔버가
        // 접촉을 잡을 기회가 없다.** 그러면 편심 충격도 없고 회전도 안 생겨서, 단단한 것에
        // 부딪힌 배가 반듯하게 멈추기만 한다.
        //
        // 예산은 감속만 하고, **정지와 회전은 솔버가 한다.** 못 뚫은 판은 살아남아 있으므로
        // 배는 줄어든 속도로 그리로 들어가고, 거기서 제대로 부딪힌다.
        // **미는 힘으로 쓴 몫은 속도를 안 깎는다.** 이미 서 있는 배에서 뺄 속도가 없다.
        // 충돌 풀에서 나간 것만 감속한다 - 이제 둘이 따로 세어져 있어서 나눌 필요가 없다.
        if (motion <= 0f)
            return;

        float joules = fromMotion / (Ballistics.DamageScale * Ballistics.RamDamageFraction);

        // **쓴 만큼을 두 운동에서 각각 뺀다.** 예산을 합쳐서 냈으니 청구서도 나눠서 물려야
        // 한다 - 선형에만 물리면 도는 힘으로 부순 배가 영영 안 느려지고, 회전에만 물리면
        // 직진으로 박은 배가 멀쩡히 계속 간다. 몫은 각자가 예산에 넣은 비율 그대로다.
        if (speed > 1e-3f)
        {
            float paid = joules * (linear / motion);

            float slowed = Mathf.Sqrt(
                Mathf.Max(0f, speed * speed - 2f * paid / Mathf.Max(1f, body.mass)));

            body.linearVelocity *= slowed / speed;
        }

        if (Mathf.Abs(omega) > 1e-4f && body.inertia > 1e-4f)
        {
            float paid = joules * (spin / motion);

            float slowed = Mathf.Sqrt(
                Mathf.Max(0f, omega * omega - 2f * paid / body.inertia));

            body.angularVelocity = slowed * Mathf.Sign(omega) * Mathf.Rad2Deg;
        }
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
        bool nested = _conducting > 0;
        Queue<Armor> wave = nested ? new Queue<Armor>() : _wave;
        HashSet<Armor> reached = nested ? new HashSet<Armor>() : _reached;

        if (!nested)
        {
            _wave.Clear();
            _reached.Clear();
        }

        wave.Enqueue(origin);
        reached.Add(origin);

        Vector2 pivot = origin.transform.position;
        Vector2 acrossAxis = new(-axis.y, axis.x);
        float cutoff = damage * cutoff01;

        _conducting++;

        try
        {
            while (wave.Count > 0 && reached.Count < maxPlates)
            {
                Armor at = wave.Dequeue();

                if (at == null)
                    continue;

                foreach (Armor neighbour in at.Neighbours)
                {
                    // == null: 이미 부서진 판. 부서진 자리로는 충격이 안 지나간다.
                    // SameBodyAs: 잔해로 갈라진 조각. 참조는 살아 있어도 이제 남의 몸이다.
                    if (neighbour == null || !at.SameBodyAs(neighbour) || reached.Contains(neighbour))
                        continue;

                    Vector2 offset = (Vector2)neighbour.transform.position - pivot;

                    // 칸이 1 m라 거리가 그대로 미터다. Abs인 이유: Unity의 접촉면 법선 부호는
                    // 콜백을 받는 쪽에 따라 뒤집힌다. 어차피 축의 양쪽으로 똑같이 번지면 된다.
                    float share = damage
                        * Mathf.Pow(along, Mathf.Abs(Vector2.Dot(offset, axis)))
                        * Mathf.Pow(across, Mathf.Abs(Vector2.Dot(offset, acrossAxis)));

                    // 더 멀리는 더 작다. 여기서 끊어도 놓치는 판이 없다.
                    if (share < cutoff)
                        continue;

                    reached.Add(neighbour);
                    neighbour.ApplyDamageEvenly(share);
                    wave.Enqueue(neighbour);
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
        origin.ApplyDamageEvenly(damage);

        Conduct(origin, Vector2.up, damage,
            Ballistics.BlastFalloff, Ballistics.BlastFalloff,
            Ballistics.BlastCutoff, Ballistics.BlastMaxPlates);

        Radiate(origin, Mathf.Sqrt(damage));
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
        Collider2D[] hits = nested ? new Collider2D[NearbyCapacity] : _nearby;

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
