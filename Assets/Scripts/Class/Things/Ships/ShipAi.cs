using UnityEngine;
using Core;

/// <summary>
/// 함선의 조종간을 잡는 AI. 조종만 한다 - 사격은 포탑마다 AiGun이 알아서 한다.
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
        _ship.engagementSign = toTarget.x >= 0f ? 1f : -1f;

        _ship.SetPilotInput(
            new Vector2(Approach(toTarget), 0f),   // y = 0: 회피 기동 없음
            Turn(toTarget));
    }

    /// <summary>
    /// FightDistance를 유지한다. 안으로 계속 파고들지 않고, 멀어지면 따라붙는다.
    /// 반환값은 월드 방향이 아니라 접근(+1)/이탈(-1)이다 - 월드 변환은 Drive()가 한다.
    /// </summary>
    private float Approach(Vector2 toTarget)
    {
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
        float want = Mathf.Atan2(toTarget.y, toTarget.x) * Mathf.Rad2Deg - 90f;
        float error = Mathf.DeltaAngle(_ship.hullAngle, want);

        // 지금 각속도로 turnLead초 동안 더 돌 각도를 미리 상쇄한다.
        float predicted = error - _ship.angleRate * turnLead;

        return Mathf.Clamp(predicted / Mathf.Max(1e-3f, turnBand), -1f, 1f);
    }
}
