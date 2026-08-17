using UnityEngine;
using Core;

/// <summary>
/// 함선의 조종간을 잡는 AI. 조종만 한다 - 사격은 포탑이 알아서 한다. PlayerInput이 없는
/// 배의 포탑은 Gun.AimMode.FollowOwner가 자동 조준으로 풀리기 때문에 여기서 할 일이 없다.
/// 그래서 이 파일에는 무기도, 명중도, 피해도 나오지 않는다.
///
/// 조종 입력은 Ship.SetPilotInput 하나로만 들어간다. PlayerInput의 OnMove/OnAngle과
/// 같은 문이라 플레이어와 AI가 서로 다른 물리를 타지 않는다 - AI가 이상하게 움직이면
/// 그건 AI 버그거나 물리 버그지, 둘 사이의 제3의 경로 때문일 수는 없다.
///
/// 틱 순서: ShipAi와 Ship은 둘 다 TickManager 리스너이고 실행 순서는 등록 순서
/// (= 하이어라키 순서)라 보장이 없다. ShipAi가 나중에 돌면 입력이 한 틱 늦게 반영되는데,
/// 60틱/초에서 16 ms이고 함선 회전은 초당 15도가 상한이라 각도로 0.25도다.
/// 맞추려고 코드를 늘릴 값이 아니다.
/// </summary>
// RequireComponent(typeof(Ship))를 안 쓴다 - Ship이 abstract라 Unity가 자동으로 붙이지 못하고
// 대신 에러를 뱉는다. 없으면 Awake에서 한 번 말하고 조용히 놀린다.
public sealed class ShipAi : TickBehaviour
{
    /// <summary>교전거리에서 이만큼은 벗어나야 추력을 넣는다. 없으면 목표 거리 위에서 떤다.</summary>
    [SerializeField] private float rangeTolerance = 5f;   // m

    /// <summary>
    /// 적이 이만큼은 옆으로 벗어나야 접근축의 부호를 바꾼다. 함선 반 척보다 넉넉해야 한다 -
    /// 충각으로 겹쳐 있는 동안 부호가 안 떨려야 하기 때문이다.
    /// </summary>
    [SerializeField] private float signDeadband = 25f;    // m

    /// <summary>조준 오차가 이 각도를 넘으면 최대 입력. 안쪽에서는 비례해 줄인다.</summary>
    [SerializeField] private float turnBand = 15f;        // deg

    /// <summary>
    /// 회전에 관성이 있어서 오차만 보고 돌리면 목표를 지나쳐 계속 진동한다. 지금 각속도로
    /// 이 시간만큼 더 갈 각도를 미리 빼서, 도착하기 전에 역분사를 시작한다.
    /// angleAccel 7.5도/초²에 종단 15도/초면 정지에 2초 - 그 값에서 출발한 숫자다.
    /// 함선 기동성을 바꾸면 여기도 같이 봐야 한다.
    /// </summary>
    [SerializeField] private float turnLead = 2f;         // s

    private Ship _ship;

    private void Awake()
    {
        _ship = GetComponent<Ship>();

        if (_ship == null)
        {
            Debug.LogError($"[ShipAi] {name}에 Ship이 없다. 이 AI는 아무것도 조종하지 않는다.", this);
            return;
        }

        // 아래 둘은 그냥 두면 "아무 일도 안 일어남"으로 나타난다. 이 프로젝트에서 제일
        // 비싼 실패 유형이라 시작할 때 한 번 크게 말한다.
        if (_ship.DetectionDistance <= 0f)
            Debug.LogError(
                $"[ShipAi] {name}의 DetectionDistance가 0이다. 탐지 반경이 0이라 적을 " +
                "영영 못 찾고, 움직이지도 쏘지도 않는다.", this);

        if (_ship.team == Ship.Team.Neutral)
            Debug.LogError(
                $"[ShipAi] {name}의 team이 Neutral이다. Neutral은 아무와도 싸우지 않으므로 " +
                "이 AI는 표적을 못 찾는다. Ally나 Enemy로 지정해라.", this);
    }

    public override void OnTick()
    {
        if (_ship == null)
            return;

        Ship target = _ship.NearestHostile();

        // 표적이 없으면 조종간을 놓는다. 마지막 입력이 남아 있으면 적을 잃은 함선이
        // 우주 저편으로 계속 가속한다.
        if (target == null)
        {
            _ship.SetPilotInput(Vector2.zero, 0f);
            return;
        }

        Vector2 toTarget = (Vector2)target.transform.position - (Vector2)transform.position;

        // Drive()가 접근축을 월드 어느 쪽으로 밀지 알려준다. 이게 없으면 적이 왼쪽에
        // 있는 함선은 주기관으로 도망간다.
        //
        // **0 근처에서 그냥 부호를 읽으면 안 된다.** 추력은 월드 x축이고 이 값은 1비트라,
        // 붙어 있는 동안 toTarget.x가 떨리면 추력이 매 틱 반대로 꺾인다 - 배가 좌우로 떨기만
        // 하고 안 들어간다. 충각으로 파고들면 두 중심의 x가 겹치므로 거기가 정확히 그 자리다.
        //
        // 그래서 확실히 반대편에 있을 때만 바꾸고, 그 안쪽에서는 마지막으로 알던 쪽을
        // 유지한다. 파고드는 중에 "저쪽은 오른쪽이었다"를 붙잡고 있는 것이 옳다.
        if (Mathf.Abs(toTarget.x) > signDeadband)
            _ship.engagementSign = toTarget.x >= 0f ? 1f : -1f;

        _ship.SetPilotInput(Thrust(toTarget), Turn(toTarget));
    }

    /// <summary>
    /// 조종 입력. **포가 있느냐 없느냐가 축의 수를 정한다.**
    ///
    /// 포함은 접근축 하나만 쓴다 - 거리만 맞추면 포탑이 알아서 조준하므로 위아래로 맞출
    /// 이유가 없다. 회피 기동은 아직 없다.
    ///
    /// 충각선은 표적에 **닿아야** 한다. 접근축만 밀면 위아래로 벌어진 만큼 그대로 옆을
    /// 스쳐 지나가고, 영영 안 부딪힌다.
    /// </summary>
    private Vector2 Thrust(Vector2 toTarget)
    {
        if (_ship.HasUsableGun)
            return new Vector2(Approach(toTarget), 0f);

        Vector2 dir = toTarget.normalized;

        // **x는 월드가 아니라 접근축이다** - Drive가 engagementSign을 곱해 월드로 보낸다.
        // 그래서 부호가 아니라 크기만 준다. 부호까지 여기서 주면 적이 왼쪽에 있을 때
        // 두 번 뒤집혀 도망간다.
        //
        // y는 월드 그대로다. Drive가 보조추진기로 밀고, 지금 엔진은 주기관과 보조가 같은
        // 출력이라 위아래로 붙는 속도가 앞뒤와 다르지 않다.
        return new Vector2(Mathf.Abs(dir.x), dir.y);
    }

    /// <summary>
    /// FightDistance를 유지한다. 안으로 계속 파고들지 않고, 멀어지면 따라붙는다.
    /// 반환값은 월드 방향이 아니라 접근(+1)/이탈(-1)이다 - 월드 변환은 Drive()가 한다.
    /// </summary>
    private float Approach(Vector2 toTarget)
    {
        // **포가 없으면 거리를 둘 이유가 없다.** 남은 무기가 뱃머리뿐인데 교전거리를 지키면
        // 그냥 맞고만 있는 배가 된다. IsCombatEffective가 이미 "포탑이 다 죽어도 움직일 수
        // 있으면 충각이 남아 있다"고 판정하는데, 그 판단이 조종에는 안 닿아 있었다.
        //
        // 거리를 0으로 두는 대신 계속 민다. RamImpact가 밀고 있는 힘을 예산으로 치기
        // 때문이다 - 붙어서 멈춘 상태에서도 힘 x 거리가 판을 부순다. 붙자마자 추력을 끊으면
        // 그 예산이 사라진다.
        if (!_ship.HasUsableGun)
            return 1f;

        float distance = toTarget.magnitude;

        if (distance > _ship.FightDistance + rangeTolerance)
            return 1f;

        if (distance < _ship.FightDistance - rangeTolerance)
            return -1f;

        return 0f;
    }

    /// <summary>
    /// 뱃머리(transform.up)를 적 쪽으로 돌린다. 포탑은 독립 선회하므로 함체 각도가
    /// 정하는 것은 어느 장갑면을 보이느냐와, 나중에 충각이 가능하냐다.
    /// </summary>
    private float Turn(Vector2 toTarget)
    {
        if (toTarget.sqrMagnitude < 1e-6f)
            return 0f;

        // -90도: Gun.Slew와 같은 이유. 뱃머리가 transform.up이라 0도가 오른쪽이 아니라 위다.
        float want = Mathf.Atan2(toTarget.y, toTarget.x) * Mathf.Rad2Deg - ((transform.localScale.x < 0)? 180f : 0f);
        float error = Mathf.DeltaAngle(_ship.hullAngle, want);

        // 지금 각속도로 turnLead초 동안 더 돌 각도를 미리 상쇄한다.
        float predicted = error - _ship.angleRate * turnLead;

        return Mathf.Clamp(predicted / Mathf.Max(1e-3f, turnBand), -1f, 1f);
    }
}
