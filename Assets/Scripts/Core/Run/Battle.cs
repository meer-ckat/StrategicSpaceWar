using System;
using UnityEngine;
using Core;

/// <summary>
/// 전투 하나. **끝나는 순간을 만드는 것이 이 클래스의 전부다.**
///
/// 지금까지 시뮬레이션은 시작도 끝도 없이 돌았다. 로그라이크의 나머지가 전부 그 순간에
/// 매달려 있다 - 피해를 저장할 시점, 수리·노획 화면이 뜰 시점, 다음 노드로 갈 시점.
///
/// **종료 조건을 enum이나 if로 두지 않는다.** 캠페인에 최소 넷이 있다:
///   섹터 전투  적대 세력에 유효 함선이 없다
///   ACT IV     통과. 모든 적을 잡을 필요가 없다
///   ACT V      거울 구조물 파괴
///   히든        탈출. 적이 계속 오는데 살아서 빠져나간다
/// 하나만 코드에 박으면 나머지 셋은 반드시 `if (isFinalStage)`로 들어온다. 술어 하나를
/// 필드로 들고 있으면 목표가 늘어도 **틱 루프는 그대로다** - 다른 함수를 대입할 뿐이다.
/// </summary>
public sealed class Battle
{
    public static Battle current;

    /// <summary>
    /// 이 전투를 끝내는 조건. 승리 쪽만 본다 - 패배는 아래에서 따로 본다.
    ///
    /// 비워두면 Awake가 <see cref="NoHostilesLeft"/>를 넣는다. 필드 초기화가 아니라 Awake인
    /// 이유는 그것이 인스턴스 메서드이기 때문이고, 인스턴스여야 하는 이유는 아래 걸쇠다.
    /// </summary>
    public Func<bool> objective;

    /// <summary>싸울 것이 없는 노드(잔해밭·보급·기항). 이겨도 전승 대사는 안 나온다 - 아무도 안 싸웠다.</summary>
    public bool peaceful;

    /// <summary>경계 없는 들판. 적을 처음 본 자리에 못을 박으면 30 km 밖 자리가 중심이 돼 플레이어를 튕긴다.</summary>
    public bool boundless;

    /// <summary>이겼는가. Ended가 참일 때만 뜻이 있다.</summary>
    public bool Won { get; private set; }

    public bool Ended { get; private set; }

    /// <summary>전투가 끝난 그 한 번. 수리·노획·항로 선택이 여기에 붙는다.</summary>
    public event Action<Battle> onEnd;

    /// <summary>
    /// 어느 전투든 끝났다. <see cref="onEnd"/>와 같은 순간이지만 **정적이라 구독자가
    /// Battle보다 먼저 살아 있어도 된다.** 인스턴스 이벤트는 current가 언제 생기는지를
    /// 구독자가 알아야 해서, 씬 로드 순서가 바뀌면 조용히 놓친다.
    /// </summary>
    public static event Action<Battle> onAnyEnd;

    /// <summary>
    /// 적을 한 번이라도 봤다. **이 걸쇠가 없으면 첫 틱에 이긴다** - "적이 없다"와 "적이
    /// 아직 안 왔다"는 같은 값으로 보이기 때문이다. 씬에 미리 놓인 배는 Awake가 첫 틱보다
    /// 앞이라 안 걸리지만, 다음 구역의 적을 한 프레임 뒤에 소환하는 순간 그 창이 열린다.
    /// </summary>
    private bool _sawHostile;

    /// <summary>
    /// 이 전투가 벌어지는 자리. 적을 **처음 본 순간** 한 번 정하고 안 움직인다.
    ///
    /// **구역이 장소가 되려면 좌표가 있어야 한다.** 지금까지 구역은 x값 하나였고, 그래서
    /// 도착도 이탈도 종료도 경계가 없었다 - "우주를 항행한다"가 아니라 "옆 가게에 간다"로
    /// 보이는 이유가 그것이다. 여기가 그 첫 경계다.
    ///
    /// 매 틱 다시 재지 않는 이유: 적이 흩어지거나 도망가면 중심이 따라 움직여서, 플레이어가
    /// 밀리는 방향이 전투 중에 바뀐다. 벽이 움직이면 벽이 아니다.
    /// </summary>
    public Vector2 Centre { get; private set; }

    /// <summary><see cref="Centre"/>가 정해졌나. 적을 보기 전에는 경계가 없다.</summary>
    public bool HasZone { get; private set; }

    /// <summary>
    /// 연료가 바닥나 아무 적에게도 못 닿는 틱이 이어진 개수. drag=0에서 새로 생긴 상황 -
    /// 예전에는 항력이 있어서 종단속도가 있었고, 못 미는 배는 그냥 멈춰 서 있어서 적이
    /// 다가올 수 있었다. drag=0이면 서로 흘러가며 벌어질 수 있다.
    ///
    /// 한 틱만 보면 안 되는 이유: 거리가 DetectionDistance 경계에서 흔들리면 매 틱
    /// 다르게 잡힌다. 몇 초 이어져야 진짜 좌초다.
    /// </summary>
    private int _strandedTicks;

    /// <summary>5초 @ 60틱. 순간적인 거리 흔들림에 안 걸리는 최소한의 여유.</summary>
    private const int StrandedTimeoutTicks = TickManager.TickRate * 5;

    public Battle()
    {
        current = this;

        // ??= 라 밖에서 이미 목표를 정해줬으면 안 건드린다. ACT IV의 "통과"처럼 적을 다
        // 잡을 필요가 없는 목표는 이 걸쇠가 필요 없고, 그건 그 목표가 알아서 할 일이다.
        objective ??= NoHostilesLeft;
    }

    public void Tick()
    {
        if (Ended)
            return;

        // 전장은 목표와 무관하게 정한다. SetZone이 NoHostilesLeft 안에만 있었더니
        // 목표가 교체된 구역(8구역 = TargetsDown)은 그 함수가 영영 안 불려서 경계 없이
        // 굴렀다 - 마지막 구역에서만 조용히 규칙이 사라지는 버그다.
        if (!HasZone && !boundless)
            SeekZone();

        // 순서가 중요하다. 목표를 먼저 본다 - 마지막 적과 서로 죽이면 그건 승리다.
        if (objective != null && objective())
            End(true);
        else if (!PlayerStillFighting() || Stranded())
            End(false);
    }

    /// <summary>적을 처음 보는 순간 전장을 못박는다. 못 찾으면 다음 틱에 다시 본다.</summary>
    private void SeekZone()
    {
        Ship player = Player();

        if (player == null)
            return;

        for (int i = 0; i < Ship.All.Count; i++)
        {
            if (player.IsHostileTo(Ship.All[i]))
            {
                SetZone(player, Ship.All[i]);
                return;
            }
        }
    }

    private void End(bool won)
    {
        Ended = true;
        Won = won;

        // 이긴 전투의 결과만 들고 간다. 진 전투는 런이 끝난 것이라 다음 함장은 새 배로 온다.
        if (won)
        {
            if (!RunState.Save(Player()))
                Debug.LogError("[Battle] 승리했는데 저장 실패. 손상이 다음 구역으로 안 넘어간다.");    
        }
        else
            RunState.Clear();

        Debug.Log($"[Battle] {(won ? "승리" : "패배")}. 기록 {RunLog.Entries.Count}줄.");

        onEnd?.Invoke(this);
        onAnyEnd?.Invoke(this);
    }

    /// <summary>
    /// 기본 목표. **새로 계산하는 것이 없다** - IsHostileTo가 이미 IsCombatEffective를
    /// 보고 있어서 잔해와 표류하는 시체는 애초에 적으로 세지 않는다.
    ///
    /// 걸쇠(<see cref="_sawHostile"/>) 때문에 인스턴스 메서드다. 적을 한 번도 못 본 동안은
    /// 절대 이겼다고 하지 않는다 - 그러지 않으면 적이 소환되기 전 틱에 전투가 끝난다.
    /// </summary>
    public bool NoHostilesLeft()
    {
        Ship player = Player();

        if (player == null)
            return false;

        for (int i = 0; i < Ship.All.Count; i++)
        {
            if (player.IsHostileTo(Ship.All[i]))
            {
                _sawHostile = true;
                return false;
            }
        }

        // 여기까지 왔으면 지금 적이 없다. 그게 승리인지 아직 안 온 것인지는 걸쇠가 안다.
        return _sawHostile;
    }

    /// <summary>
    /// 전장을 못박는다. 플레이어와 처음 본 적의 중간이다 - 어느 한쪽에 붙이면 그쪽이
    /// 벽에 기대고 싸운다.
    /// </summary>
    private void SetZone(Ship player, Ship hostile)
    {
        Centre = ((Vector2)player.transform.position + (Vector2)hostile.transform.position) * 0.5f;
        HasZone = true;
    }

    /// <summary>
    /// 아직 싸울 수 있는 플레이어 함선이 있는가. 없으면 런이 끝난다 - 죽은 함장은 안 돌아오고
    /// 다음에는 다른 함장이 출항한다.
    /// </summary>
    private static bool PlayerStillFighting()
    {
        Ship player = Player();

        return player != null && player.IsCombatEffective;
    }

    /// <summary>
    /// 플레이어가 못 움직이고, 닿는 적도 없다. 걸쇠(<see cref="_strandedTicks"/>)로
    /// 몇 초 이어져야 참이 된다 - <see cref="_sawHostile"/>과 같은 이유로, 상태가 아니라
    /// **지속된** 상태가 사건이다.
    ///
    /// 패배로 친다. 오너의 판단 - 플레이어의 목표는 끝까지 나아가는 것이라, 못 움직이고
    /// 적도 못 닿으면 그 자체로 임무 실패다. 무력한 채 살아있는 것을 승리로 치지 않는다.
    /// </summary>
    private bool Stranded()
    {
        Ship player = Player();

        // 탱크가 하나도 없으면 Drive()가 무제한이라 이 배는 애초에 좌초할 수 없다 -
        // 배치를 아직 안 끝낸 배(지금 다섯 함급 전부)가 여기 걸리면 안 된다.
        if (player == null || player.shipTanks.Count == 0 || player.AvailableDeltaV() > 0f)
        {
            _strandedTicks = 0;
            return false;
        }

        for (int i = 0; i < Ship.All.Count; i++)
        {
            Ship other = Ship.All[i];

            if (other == null || !player.IsHostileTo(other))
                continue;

            // DetectionDistance가 기준이다 - NearestHostile()이 같은 반경을 쓰므로,
            // 이 밖에 있으면 포탑도 애초에 그 적을 조준 대상으로 못 잡는다. 못 움직이는데
            // 조준도 못 하면 할 수 있는 일이 없다.
            float dist = Vector2.Distance(player.transform.position, other.transform.position);

            if (dist <= player.DetectionDistance)
            {
                _strandedTicks = 0;
                return false;
            }
        }

        _strandedTicks++;
        return _strandedTicks >= StrandedTimeoutTicks;
    }

    private static Ship Player()
    {
        for (int i = 0; i < Ship.All.Count; i++)
        {
            if (Ship.All[i] != null && Ship.All[i].IsPlayerControlled)
                return Ship.All[i];
        }

        return null;
    }
}
