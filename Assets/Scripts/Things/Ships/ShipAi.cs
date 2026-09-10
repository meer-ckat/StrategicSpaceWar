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
    /// <summary>
    /// 거리 오차가 이만큼 벌어지면 최대 추력. 안쪽에서는 비례해 줄인다 - <see cref="turnBand"/>와
    /// 같은 구조이고, 축이 각도 대신 거리일 뿐이다.
    ///
    /// **죽은 구역(deadband)이 아니다.** 예전에는 "이 안에서는 추력 0"이었는데, 그러면
    /// 목표 거리 근처에서 추력이 1과 0 사이를 계단으로 오간다 - 켜면 지나치고, 지나치면
    /// 반대로 켜고, 그 사이는 무추력이라 관성으로 흘러간다. 비례로 두면 목표에 가까울수록
    /// 저절로 약해져서 drag가 잡아 준다.
    /// </summary>
    [SerializeField] private float approachBand = 40f;    // m

    /// <summary>조준 오차가 이 각도를 넘으면 최대 입력. 안쪽에서는 비례해 줄인다.</summary>
    [SerializeField] private float turnBand = 15f;        // deg

    /// <summary>
    /// 회전에 관성이 있어서 오차만 보고 돌리면 목표를 지나쳐 계속 진동한다. 지금 각속도로
    /// 이 시간만큼 더 갈 각도를 미리 빼서, 도착하기 전에 역분사를 시작한다.
    /// angleAccel 7.5도/초²에 종단 15도/초면 정지에 2초 - 그 값에서 출발한 숫자다.
    /// 함선 기동성을 바꾸면 여기도 같이 봐야 한다.
    /// </summary>
    [SerializeField] private float turnLead = 2f;         // s

    /// <summary>
    /// Turn()의 turnLead와 같은 구조, 축이 각도 대신 거리다. drag가 있을 때는 목표 거리
    /// 근처에서 항력이 알아서 속도를 죽여줘서 이게 없어도 버텼다. drag=0이면 감쇠가
    /// 순수 추력뿐이라, 지금 접근 속도를 미리 안 빼면 목표 거리에서 못 멈추고 그대로
    /// 뚫고 지나간다 - 반대편에서 다시 미는 것을 반복하며 서로 스쳐 지나가는 진동이 된다.
    /// 함선 가속도를 바꾸면 turnLead처럼 이 값도 같이 봐야 한다.
    /// </summary>
    [SerializeField] private float approachLead = 2f;     // s
    public bool _detatchBrain = false;
    public Vector3? _targetPos = null;

    /// <summary>
    /// 컷신이 못박은 함체 각도(도). 있으면 표적이 아니라 이 각도로 돈다 - 연출은 "적함이
    /// 이쪽으로 뱃머리를 튼다"를 표적과 무관하게 시킬 수 있어야 한다. null이면 평소대로
    /// <see cref="_targetPos"/>를 본다.
    /// </summary>
    public float? _faceAngle = null;

    /// <summary>
    /// 쫓을 대상. 있으면 <see cref="_targetPos"/>를 매 틱 다시 계산한다 - 한 점으로 보내는
    /// moveTo는 **실행 시점 좌표를 고정**하므로, 목표가 움직이면 빈 자리를 친다.
    ///
    /// **충각에는 리드가 필요하다.** 포는 안 그렇다(탄속 900에 교전거리 40이면 리드가
    /// 0.45 m라 함선보다 작다) - 그런데 창은 452 m/s로 800 m를 날아가서 비행시간이 2초다.
    /// 그 사이 회피하는 배는 통째로 옆으로 빠진다.
    /// </summary>
    public Transform _chase = null;

    /// <summary>
    /// 편대장. 있으면 매 틱 <see cref="_formationOffset"/>만큼 떨어진 자리를 다시 계산해서
    /// 그리로 간다 - <c>moveTo</c>가 실행 시점 좌표를 고정하는 것과 정반대고, <c>chase</c>와
    /// 달리 **붙는 게 아니라 간격을 지킨다.**
    /// </summary>
    public Transform _formation = null;

    /// <summary>
    /// 편대장 기준 자리(m). **편대장의 좌표계다** - 편대장이 돌면 편대가 통째로 같이 돈다.
    /// 실제 편대 비행이 그렇고, 무엇보다 안정적이다: 자기 자신을 기준으로 잡으면
    /// "적을 조준하려고 돌았더니 목표 자리가 움직이고, 그리로 가면 또 돈다"는 피드백
    /// 루프가 생겨서 적이 옆에 있을 때 제자리를 빙빙 돈다.
    /// </summary>
    public Vector2 _formationOffset = Vector2.zero;

    /// <summary>쫓는 대상의 몸. 속도를 읽으려고 든다 - 매 틱 GetComponent를 안 하려는 캐시.</summary>
    private Rigidbody2D _chaseBody;
    private Transform _chaseCached;

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

        if(_detatchBrain == false)
        {
            var tgt = _ship.NearestHostile();
            _targetPos = tgt != null? tgt.transform.position : null;

            // 표적이 없으면 조종간을 놓는다. 마지막 입력이 남아 있으면 적을 잃은 함선이
            // 우주 저편으로 계속 가속한다.
        }
        else if (_formation != null)
        {
            _targetPos = FormationSlot();
        }
        else if (_chase != null)
        {
            _targetPos = LeadPoint();
        }

        if (_targetPos == null)
        {
            _ship.SetPilotInput(Vector2.zero, 0f);
            _ship.pilotBoost = false;
            return;
        }

        makeInput((Vector2)_targetPos);
    }

    /// <summary>
    /// 쫓는 대상이 **도착할 자리**. 지금 있는 자리가 아니다.
    ///
    /// 비행시간을 거리 / 내 속도로 잡고 그만큼 앞을 겨눈다. 한 번만 푸는 것이 요점이다 -
    /// 목표도 움직이므로 정확한 해는 반복이 필요한데, 충각은 스치기만 해도 성립하는
    /// 판정이라 한 번이면 함선 크기 안으로 들어온다.
    ///
    /// 대상이 죽으면(오브젝트가 사라지면) 마지막으로 알던 점을 그대로 들고 간다 - 쫓던
    /// 것이 없어졌다고 그 자리에 서면, 관통하고 지나가야 할 창이 시체 위에서 멈춘다.
    /// </summary>
    private Vector3? LeadPoint()
    {
        if (_chase == null)
            return _targetPos;

        // 대상이 바뀔 때만 몸을 다시 찾는다.
        if (_chaseCached != _chase)
        {
            _chaseCached = _chase;
            _chase.TryGetComponent(out _chaseBody);
        }

        Vector2 here = transform.position;
        Vector2 there = _chase.position;

        Vector2 theirVelocity = _chaseBody != null ? _chaseBody.linearVelocity : Vector2.zero;

        // 내 속도가 0에 가까우면 비행시간이 무한이 된다. 아직 가속 중인 창이 그 경우라,
        // 리드가 화면 밖으로 튀지 않게 상한을 둔다.
        float mySpeed = Mathf.Max(1f, _ship.velocity.magnitude);
        float flight = Mathf.Min((there - here).magnitude / mySpeed, MaxLeadSeconds);

        return (Vector3)(there + theirVelocity * flight);
    }

    /// <summary>리드 상한(초). 아직 느린 창이 표적 한참 앞의 허공을 겨누는 것을 막는다.</summary>
    private const float MaxLeadSeconds = 4f;

    /// <summary>
    /// 이보다 멀면 부스터를 켠다(m). <see cref="approachBand"/>(40)의 두 배 남짓이라
    /// "밴드를 완전히 벗어나 한참 뒤처졌다"는 뜻이다 - 편대 간격(60~75 m)보다 커야
    /// 자리에 붙어 있는 동안 안 켜진다.
    /// </summary>
    private const float BoostDistance = 100f;

    /// <summary>
    /// 편대장 기준 내 자리. **편대장의 자세로 오프셋을 돌린다** - 편대장이 뱃머리를 틀면
    /// 편대가 통째로 따라 돌고, 그래서 "왼쪽 뒤"가 계속 왼쪽 뒤로 남는다.
    ///
    /// 리드를 안 준다. 자리 자체가 편대장을 따라 움직이므로 쫓아갈 것이 없고, 따라붙는
    /// 일은 <see cref="Approach"/>의 approachLead가 이미 한다.
    /// </summary>
    private Vector3? FormationSlot()
    {
        if (_formation == null)
            return _targetPos;

        // 좌우 반전된 편대장(localScale.x = -1)에서도 "오른쪽"이 눈에 보이는 오른쪽이어야
        // 한다. Ship.NoseDirection이 같은 보정을 들고 있으므로 있으면 그걸 쓴다.
        Vector2 right = _formation.TryGetComponent(out Ship lead)
            ? lead.NoseDirection
            : (Vector2)_formation.right;

        Vector2 up = new(-right.y, right.x);

        return (Vector3)((Vector2)_formation.position
            + right * _formationOffset.x
            + up * _formationOffset.y);
    }

    void makeInput(Vector2 target)
    {
        Vector2 toTarget = target - (Vector2)transform.position;

        _ship.SetPilotInput(Thrust(toTarget), Turn(toTarget));

        // **최대 추력으로도 모자랄 때만 켠다.** Approach는 오차를 approachBand로 나눠
        // 자르므로, 1에 붙어 있다는 것은 "이미 전부 밀고 있는데 아직 멀다"는 뜻이다.
        // 그 지점이 부스터가 있는 이유고, 문턱을 따로 안 적어도 밴드가 이미 정해 준다.
        //
        // **다가갈 때만이다.** 물러설 때(-1)도 예산은 모자라지만, 편대기가 자리를
        // 지나쳐 놓고 부스터로 되돌아오면 진동이 커진다 - 그쪽은 천천히 붙는 게 맞다.
        //
        // 거리 조건이 따로 붙는 이유: chase는 Approach가 늘 1f라(충각선은 감속하지
        // 않는다) 그것만 보면 **영구 부스트**가 되어 붙어 있는 동안에도 연료를 태운다.
        // 실제로 모자란 것은 "멀다"일 때뿐이다.
        _ship.pilotBoost = Approach(toTarget) >= 1f
            && toTarget.sqrMagnitude > BoostDistance * BoostDistance;
    }

    /// <summary>
    /// 조종 입력. **월드 방향을 그대로 준다** - Ship.thrustInput이 월드 축이고 Drive()가
    /// 함체 각도를 안 섞으므로, 여기서 변환할 것이 없다. 표적을 잇는 선 위로 밀 뿐이고
    /// 부호는 <see cref="Approach"/>가 든다(멀면 접근, 가까우면 이탈, 목표 거리에서 0).
    ///
    /// 예전에는 월드 x축에 **크기만** 넘기고 좌우는 `engagementSign` 1비트로 복원했다.
    /// 그 1비트에 25 m 데드밴드가 걸려 있어서 짧은 이동에서는 부호가 얼어붙었고, 컷신의
    /// 15 m짜리 moveTo가 정확히 그 자리였다 - 벡터를 그대로 들고 가면 압축했다 푸는
    /// 왕복 자체가 없다.
    ///
    /// 회피 기동은 아직 없다.
    /// </summary>
    private Vector2 Thrust(Vector2 toTarget)
    {
        return toTarget.normalized * Approach(toTarget);
    }

    /// <summary>
    /// FightDistance를 유지한다. 안으로 계속 파고들지 않고, 멀어지면 따라붙는다.
    ///
    /// 반환값은 방향이 아니라 **부호 붙은 세기**다 - 접근(+1) / 이탈(-1)이고 그 사이의
    /// 값도 나온다. 방향은 <see cref="Thrust"/>가 `toTarget.normalized`로 이미 들고
    /// 있어서, 이 값을 곱하면 표적을 잇는 선 위의 월드 벡터가 된다.
    /// </summary>
    private float Approach(Vector2 toTarget)
    {
        // **포가 없으면 거리를 둘 이유가 없다.** 남은 무기가 뱃머리뿐인데 교전거리를 지키면
        // 그냥 맞고만 있는 배가 된다. IsCombatEffective가 이미 "포탑이 다 죽어도 움직일 수
        // 있으면 충각이 남아 있다"고 판정하는데, 그 판단이 조종에는 안 닿아 있었다.
        //
        // 거리를 0으로 두는 대신 계속 민다. RamImpact가 밀고 있는 힘을 예산으로 치기
        // 때문이다 - 붙어서 멈춘 상태에서도 힘 x 거리가 판을 부순다. 붙자마자 추력을 끊으면
        // 그 예산이 사라진다. **여기만 1f 고정인 이유가 그것이다** - 충각선에는 유지할
        // 거리가 없으므로 비례할 오차 자체가 없다.
        //
        // **쫓는 배는 감속하지 않는다.** chase는 정의상 들이받으러 가는 것이라 표적 위에서
        // 멈출 이유가 없고, RamImpact가 **밀고 있는 힘**을 피해 예산으로 치므로 닿는
        // 순간까지 최대로 밀어야 한다 - 붙자마자 추력을 끊으면 그 예산이 사라진다.
        //
        // 이게 없으면 approachLead가 정확히 거꾸로 일한다: 컷신 배는 지킬 거리가 0이라
        // 가까워질수록 error가 0으로 가는데 거기서 접근속도 x 2초를 또 빼서 음수가 되고,
        // 창이 표적 앞에서 역분사한다. 비례 감속은 "자리를 지키는" 지시(moveTo·formation·
        // 교전거리)의 것이지 "들이받는" 지시의 것이 아니다.
        if (_chase != null)
            return 1f;

        // **컷신의 moveTo는 이 분기를 안 탄다.** 컷신 배는 싸우는 게 아니라 정해진 자리로
        // 가는 것이라, 포가 없다고 목적지를 지나쳐 계속 밀면 안 된다.
        if (!_detatchBrain && !_ship.HasUsableGun)
            return 1f;

        // Turn과 같은 꼴이다: 오차를 밴드로 나누고 자른다. 멀면 +(접근), 가까우면 -(이탈),
        // 목표 거리 위에서는 0이 저절로 나온다 - 따로 죽은 구역을 둘 필요가 없다.
        //
        // **컷신일 때 _targetPos는 적이 아니라 목적지다.** 그래서 지킬 거리가 0이고,
        // 배는 그 점 위에서 멈춘다 - 교전처럼 FightDistance만큼 떨어져 서면 연출이
        // 지정한 자리와 매번 어긋난다.
        float want = _detatchBrain ? 0f : _ship.FightDistance;
        float error = toTarget.magnitude - want;

        // 다가가는 속도(표적 쪽으로 좁히는 성분)만큼, 지금 속도로 approachLead초 더 가면
        // 줄어들 거리를 미리 뺀다. Turn()의 angleRate * turnLead와 정확히 같은 구조다.
        float closingSpeed = Vector2.Dot(_ship.velocity, toTarget.normalized);
        float predicted = error - closingSpeed * approachLead;

        return Mathf.Clamp(predicted / Mathf.Max(1e-3f, approachBand), -1f, 1f);
    }

    /// <summary>
    /// 뱃머리(transform.up)를 적 쪽으로 돌린다. 포탑은 독립 선회하므로 함체 각도가
    /// 정하는 것은 어느 장갑면을 보이느냐와, 나중에 충각이 가능하냐다.
    /// </summary>
    private float Turn(Vector2 toTarget)
    {
        // 컷신이 각도를 못박았으면 표적을 안 본다. 아래 거리 검사보다 위에 있어야 한다 -
        // 목적지 위에 서 있는(거리 0) 배도 각도 지시는 따라야 하기 때문이다.
        if (_faceAngle.HasValue)
        {
            float turnTo = Mathf.DeltaAngle(_ship.hullAngle, _faceAngle.Value)
                - _ship.angleRate * turnLead;

            return Mathf.Clamp(turnTo / Mathf.Max(1e-3f, turnBand), -1f, 1f);
        }

        // **편대는 편대장과 같은 곳을 본다.** 자리에 도착하면 toTarget이 0이라 아래
        // 분기가 각도를 놓아버리는데, 그러면 편대기가 마지막 각도로 얼어붙어 배마다
        // 제멋대로 돌아간 채 따라다닌다. 편대 비행은 나란히 나는 것이 그림이다.
        if (_formation != null)
        {
            float lead = Mathf.DeltaAngle(_ship.hullAngle, _formation.eulerAngles.z)
                - _ship.angleRate * turnLead;

            return Mathf.Clamp(lead / Mathf.Max(1e-3f, turnBand), -1f, 1f);
        }

        if (toTarget.sqrMagnitude < 1e-6f)
            return 0f;

        // -90 아니다 - 뱃머리는 transform.up이 아니라 +X다(NoseDirection 참고, Gun과 다른
        // 축). 여기서 빼는 180은 각도 보정이 아니라 좌우 반전 배의 거울상 보정이다.
        float want = Mathf.Atan2(toTarget.y, toTarget.x) * Mathf.Rad2Deg - ((transform.localScale.x < 0)? 180f : 0f);
        float error = Mathf.DeltaAngle(_ship.hullAngle, want);

        // 지금 각속도로 turnLead초 동안 더 돌 각도를 미리 상쇄한다.
        float predicted = error - _ship.angleRate * turnLead;

        return Mathf.Clamp(predicted / Mathf.Max(1e-3f, turnBand), -1f, 1f);
    }
}
