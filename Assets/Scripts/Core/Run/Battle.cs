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

        // 순서가 중요하다. 목표를 먼저 본다 - 마지막 적과 서로 죽이면 그건 승리다.
        if (objective != null && objective())
            End(true);
        else if (!PlayerStillFighting())
            End(false);
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
    /// 아직 싸울 수 있는 플레이어 함선이 있는가. 없으면 런이 끝난다 - 죽은 함장은 안 돌아오고
    /// 다음에는 다른 함장이 출항한다.
    /// </summary>
    private static bool PlayerStillFighting()
    {
        Ship player = Player();

        return player != null && player.IsCombatEffective;
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
