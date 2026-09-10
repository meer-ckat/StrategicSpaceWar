using UnityEngine;

public class Launcher : Gun
{
    /// <summary>조준점에서 이 반경(m) 안의 몸을 표적으로 잠근다.</summary>
    public float LockRadius = 40f;

    /// <summary>표적이 없어도 쏜다. 유도는 미사일 자신(FireAndForget)에게 맡긴다.</summary>
    public bool NoTargeting = false;

    private static readonly Collider2D[] _lock = new Collider2D[16];

    /// <summary>
    /// 잠금 재검색 간격(틱). 400m OverlapCircle은 전장 절반을 브로드페이즈에 태우는
    /// 값이라 매 틱 돌리면 그것만으로 프레임이 죽는다(실측 35ms). 0.1초 낡은 잠금은
    /// 표적이 몇 m 움직인 오차일 뿐이고, 발사 뒤의 추적은 어차피 미사일 몫이다.
    /// </summary>
    private const int AcquireInterval = 6;

    private Transform _acquired;
    /// <summary>
    /// 마지막 잠금 검색 틱. **long.MinValue를 쓰면 안 된다** - 간격 검사가 뺄셈이라
    /// `0 - long.MinValue`가 부호 있는 오버플로로 감겨 음수가 되고, 그 값은 tick이
    /// 아무리 커져도 AcquireInterval 밑에 머문다. **검색이 영원히 한 번도 안 돈다.**
    ///
    /// 증상이 예외도 로그도 아니고 "잠금이 안 잡힌다" 하나뿐이라, NoTargeting 발사대가
    /// 단락 평가로 Acquire를 아예 안 부르는 동안(= 지금까지 전부) 아무도 못 봤다.
    /// -AcquireInterval이면 첫 틱에 바로 한 번 돌고 오버플로가 없다.
    /// </summary>
    private long _acquireTick = -AcquireInterval;

    /// <summary>
    /// 표적이 안 잠기면 발사 자체를 미룬다. WantsToFire가 false면 Gun.OnTick이
    /// _pending을 소모하지 않으므로, 잠기는 순간 모아둔 발이 바로 나간다 -
    /// 사선 검사(LineIsClear)와 같은 리듬이다.
    ///
    /// **NoTargeting이면 잠금을 아예 안 본다** - 검색 비용도 없고, "잠금 실패 = 발사
    /// 금지"가 뒤집혀 미사일이 영영 안 나가는 일도 없다.
    /// </summary>
    protected override bool WantsToFire =>
        base.WantsToFire && (NoTargeting || Acquire() != null);

    /// <summary>
    /// 미사일은 사선 검사를 안 한다. 포탄과 달리 발사 직후 유도로 궤적을 트는데다
    /// 초기 방향이 잠금 표적이 아니라 포신이라, 근접한 아군을 스치는 각도로 뜨는 일이
    /// 흔하다 - 그때마다 발사를 미루면 사실상 못 쏘는 발사대가 된다.
    /// </summary>
    protected override bool LineIsClear() => true;

    /// <summary>
    /// 지금 잠긴 표적. HUD가 여기에 십자를 찍는다. **읽기만 한다** - 갱신은
    /// <see cref="OnTick"/> 하나이고, OnGUI에서 <see cref="Acquire"/>를 부르면
    /// 400m OverlapCircle이 틱이 아니라 화면 주사율을 타게 된다.
    /// </summary>
    public Transform Locked => _acquired;

    /// <summary>
    /// **잠금은 방아쇠보다 먼저 돈다.** <see cref="WantsToFire"/> 안에서만 Acquire를
    /// 부르면 단락 평가 때문에 마우스를 누르는 동안에만 갱신된다 - 쏘기 전에 무엇이
    /// 잠겼는지 볼 수가 없는데, 그게 정확히 함장이 알고 싶은 순간이다.
    ///
    /// 간격 캐시(<see cref="AcquireInterval"/>)가 그대로라 검색 횟수는 안 늘고,
    /// 같은 틱 안에서 WantsToFire가 다시 불러도 캐시를 읽는다.
    /// </summary>
    public override void OnTick()
    {
        if (!Neutralized)
        {
            if (NoTargeting)
                AcquireForward();
            else
                Acquire();
        }

        base.OnTick();
    }

    /// <summary>
    /// NoTargeting 발사대의 잠금. **격발 조건이 아니라 표시 전용이다** - 이 발사대는
    /// 잠금과 무관하게 쏘고, 무엇을 물지는 <see cref="Missile.Launch"/>가 발사 순간
    /// 정한다. 그래서 미리 보여주려면 그쪽과 **같은 함수**를 물어야 한다.
    ///
    /// <see cref="Acquire"/>의 400m OverlapCircle과 달리 Ship.All 훑기라 싸지만,
    /// <see cref="MuzzleShot"/>이 부모를 거슬러 Rigidbody를 찾으므로 같은 간격으로 묶는다.
    /// </summary>
    private void AcquireForward()
    {
        long tick = Core.TickManager.currentTick;

        if (tick - _acquireTick < AcquireInterval)
            return;

        _acquireTick = tick;

        MuzzleShot(out Vector2 muzzle, out _, out _);

        // **원뿔의 축은 포신이지 비행 방향이 아니다.** Fire가 Launch에 넘기는 것이
        // _turret.up이라 그쪽을 그대로 써야 답이 같다. MuzzleShot의 direction(포신 x
        // 탄속 + 포구 속도)을 쓰면 MinuteMan처럼 muzzleSpeed가 2인 미사일에서 축이
        // 사실상 배의 진행 방향이 돼서, 십자가 미사일과 딴 데를 가리킨다.
        _acquired = Missile.PickForward(muzzle, _turret.up, GetComponentInParent<Rigidbody2D>());
    }

    /// <summary>
    /// 조준점 근처에서 가장 가까운 남의 몸. 포구 레이로 꿰는 방식은 조준선이 표적
    /// 콜라이더를 정확히 지나야만 잠겼다 - 커서가 몇 m만 비껴도 null이었다.
    /// 몸통(Rigidbody)을 잡는 이유는 판 하나를 잡으면 그 판이 죽는 순간 표적도
    /// 같이 사라져서다.
    ///
    /// 결과는 <see cref="AcquireInterval"/>틱 동안 캐시된다 - WantsToFire(매 틱)와
    /// Fire(격발 틱)가 같은 값을 나눠 쓰고, 검색은 간격마다 한 번만 돈다.
    /// </summary>
    private Transform Acquire()
    {
        long tick = Core.TickManager.currentTick;

        if (tick - _acquireTick < AcquireInterval)
            return _acquired;

        _acquireTick = tick;
        _acquired = AcquireNow();
        return _acquired;
    }

    private Transform AcquireNow()
    {
        if (!TryGetTarget(out Vector2 aim))
            return null;

        Rigidbody2D body = GetComponentInParent<Rigidbody2D>();
        Transform target = null;
        float best = float.MaxValue;

        int n = Physics2D.OverlapCircleNonAlloc(aim, LockRadius, _lock);

        for (int i = 0; i < n; i++)
        {
            Collider2D c = _lock[i];

            if (body != null && c.attachedRigidbody == body)
                continue;

            // **판정은 IsHostileTo 하나다.** 자기 몸만 걸렀을 때는 조준점 근처의 편대
            // 윙맨이 그대로 잠겼다 - 팀 비교를 여기 손으로 적으면 그 술어가 두 벌이 되고,
            // 중립(운석)과 시체(IsCombatEffective) 조건은 또 빠진다. Ship이 아닌 몸
            // (Hulk - 잔해·거울)은 예전처럼 잠긴다: 8구역 표적이 그것이다.
            if (owner != null && c.attachedRigidbody != null)
            {
                Ship theirs = c.attachedRigidbody.GetComponent<Ship>();

                if (theirs != null && !owner.IsHostileTo(theirs))
                    continue;
            }

            float d = ((Vector2)c.transform.position - aim).sqrMagnitude;

            if (d < best)
            {
                best = d;
                target = c.attachedRigidbody != null ? c.attachedRigidbody.transform : c.transform;
            }
        }

        return target;
    }

    protected override void Fire()
    {
        // WantsToFire와 같은 캐시를 읽는다 - 같은 간격 안이라 공짜다.
        Transform target = Acquire();

        if (target == null && !NoTargeting)
            return;

        MuzzleShot(out Vector2 muzzle, out _, out Vector2 inherited);

        Vector2 direction = _turret.up;

        var shell = DefDatabase.Spawn(projectile, null, muzzle, _turret.eulerAngles.z) as Projectile;

        if (shell == null)
            return;

        Rigidbody2D body = GetComponentInParent<Rigidbody2D>();

        // 산개 방향은 발사대가 배의 어느 쪽에 붙었는지로 정한다 - 위쪽 발사대는 위로,
        // 아래쪽은 아래로 갈라진다. 배 로컬 좌표라 좌우 반전(localScale.x=-1)도 따라온다.
        // 포탑은 판의 자식이라 transform.localPosition은 판 기준이다 - 배 기준이 필요하다.
        if (shell is Missile missile && owner != null)
            missile.side = owner.transform.InverseTransformPoint(transform.position).y >= 0f ? 1f : -1f;

        shell.Launch(direction, muzzleSpeed, inherited, body, target);

        if (body != null)
        {
            body.AddForceAtPosition(
                -direction * (shell.mass * muzzleSpeed * Ballistics.RecoilScale),
                muzzle,
                ForceMode2D.Impulse);
        }

        SoundManager.AudioShot("Cannon", muzzle);
    }
}
