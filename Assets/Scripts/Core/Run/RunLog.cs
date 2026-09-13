using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 이번 런에서 일어난 일. **추가만 하고 지우지 않는다.**
///
/// 엔딩이 마지막 선택지 셋이 아니라 런 전체다 - 무엇을 죽였고 누구를 살렸고 어떤 정보를
/// 회수했는지가 나중에 어떤 엔딩이 열리는지를 정한다. 그래서 "그 순간"에 적어 두지 않으면
/// 나중에 되살릴 방법이 없다.
///
/// **스키마를 지금 정하지 않는다.** 무엇이 필요한지는 ACT II 이벤트를 짜봐야 안다. 지금은
/// 사건 하나가 종류 + 이름 + 팀이고, 부족해지면 그때 필드를 늘린다. 미리 설계하면 반드시
/// 안 쓰는 필드가 절반이다.
/// </summary>
public static class RunLog
{
    public enum Kind
    {
        /// <summary>함선이 전투불능이 된 순간. 격침이 아니라 "더 이상 싸울 수 없음"이다.</summary>
        Finished,

        /// <summary>탄약고나 원자로가 유폭했다. <c>what</c>은 그 모듈 이름이다.</summary>
        Detonated,

        /// <summary>선체가 갈라져 조각이 떨어져 나갔다.</summary>
        HullSplit,

        /// <summary>승무원이 전멸했다. 되돌릴 수 없고, 배는 그 순간부터 표류물이다.</summary>
        CrewLost,

        /// <summary>플레이어 탄약 25% 아래. <c>what</c>은 남은 발수.</summary>
        AmmoLow,

        /// <summary>플레이어 탄약 0.</summary>
        AmmoOut,

        /// <summary>급여. <c>what</c>은 금액.</summary>
        Paid,

        /// <summary>보급 자리에서 실었다. <c>what</c>은 제일 많이 받은 것.</summary>
        Supplied,

        /// <summary>
        /// 역할 하나가 끊겼다. <c>what</c>은 함선 이름이 아니라 <see cref="Ship.ShipRole"/>
        /// 이름이다 - 이 사건만 그렇다.
        ///
        /// 함선 이름을 버리는 것이 손해처럼 보이는데, 이 사건을 읽는 쪽이 대사이고 대사는
        /// **한 승무원의 무전**이라 "누구의 기관사인가"에 답이 이미 있다. <c>team</c>이
        /// 아군/적군을 가르므로 그 이상은 안 쓴다. 배 이름까지 넣으려면 Entry에 필드가
        /// 하나 늘고, 그건 나머지 네 사건 전부가 빈칸으로 들고 다녀야 하는 값이다.
        /// </summary>
        RoleLost,

        /// <summary>
        /// 판 하나가 **처음** 뚫렸다. <c>what</c>은 그 판이 있는 구역 이름이다.
        ///
        /// **초당 100발이 초당 100줄이 되지 않는 이유가 걸쇠다.** <see cref="Armor.AnyBreached"/>는
        /// 그 판의 서브셀 하나가 처음 죽을 때만 서고 다시 눕지 않는다 - 같은 판에 백 발이
        /// 더 박혀도 한 줄이다. 필터를 새로 만든 것이 아니라 시뮬이 이미 들고 있던 걸쇠를
        /// 읽는 것뿐이다.
        /// </summary>
        Penetrated,

        /// <summary>
        /// 기밀 구획이 우주로 열렸다. <c>what</c>은 구역 이름이다.
        ///
        /// 판 소실로 판정한다(관통이 아니라). 서브셀 하나는 17cm짜리 구멍이라 공기는 새도
        /// 승무원이 "격실이 뚫렸다"고 말할 사건이 아니다 - <see cref="Room.wallsSurfaced"/>가
        /// 정확히 그 경계에 있는 걸쇠라 그것을 그대로 쓴다.
        /// </summary>
        RoomBreached,

        /// <summary>구역에 들어섰다. <c>what</c>은 1부터 세는 구역 번호다 - 대본 키가 그대로 쓴다.</summary>
        SectorEntered,

        /// <summary>구역을 이겼다. <c>what</c>은 <see cref="SectorEntered"/>와 같은 번호다.</summary>
        SectorCleared,
    }

    public readonly struct Entry
    {
        public readonly Kind kind;
        public readonly string what;
        public readonly Ship.Team team;
        public readonly long tick;

        public Entry(Kind kind, string what, Ship.Team team, long tick)
        {
            this.kind = kind;
            this.what = what;
            this.team = team;
            this.tick = tick;
        }

        public override string ToString() => $"[{tick}] {kind} {what} ({team})";
    }

    private static readonly List<Entry> _entries = new();

    public static IReadOnlyList<Entry> Entries => _entries;

    /// <summary>
    /// 기록이 하나 늘었다. **UI가 시뮬레이션을 구독하는 방향이다** - 반대로 하면 배가
    /// 터지는 코드가 대사창을 알아야 한다.
    ///
    /// 여기 붙는 것이 <see cref="Finished"/>의 걸쇠를 그대로 물려받는 것이 중요하다.
    /// 사건이 정확히 한 번 오므로 구독자가 중복을 따로 막을 필요가 없다.
    /// </summary>
    public static event Action<Entry> onEntry;

    /// <summary>
    /// 함선이 전투불능이 된 순간. **격침이 아니다** - 승무원이 질식했든 원자로가 나갔든
    /// 포탑이 전멸했든, 이 배가 더 이상 상대가 아니게 된 그 한 번이다.
    ///
    /// 부르는 자리가 <see cref="Ship.WatchForCritical"/> 하나뿐인 것이 중요하다. 거기에
    /// 이미 걸쇠가 있어서 상태가 아니라 전이를 잡는다 - 매 틱 IsCombatEffective를 읽으면
    /// 같은 죽음을 60번 적고, 원자로를 수리해 되살아난 배의 죽음까지 남는다.
    /// </summary>
    public static void Finished(Ship ship)
    {
        if (ship == null)
            return;

        string what = string.IsNullOrEmpty(ship.shipDefName) ? ship.name : ship.shipDefName;

        Add(Kind.Finished, what, ship.team);
    }

    /// <summary>
    /// 선체가 갈라졌다. <see cref="Ship.SplitIfBroken"/>이 <c>TrySplitIfBroken</c>의 반환값이
    /// true일 때만 부르므로, 판이 죽을 때마다가 아니라 **실제로 조각이 떨어진 틱**에만 온다.
    /// 한 틱에 조각이 둘 이상 떨어져도 한 번이다.
    /// </summary>
    public static void HullSplit(Ship ship)
    {
        if (ship != null)
            Add(Kind.HullSplit, NameOf(ship), ship.team);
    }

    /// <summary>
    /// 승무원 전멸. <see cref="Ship.Crew"/>의 <c>CrewAlive</c>가 걸쇠라 한 번만 온다 -
    /// 재가압해도 죽은 사람은 안 돌아오므로 다시 참이 될 일이 없다.
    /// </summary>
    public static void CrewLost(Ship ship)
    {
        if (ship != null)
            Add(Kind.CrewLost, NameOf(ship), ship.team);
    }

    /// <summary>
    /// 유폭. **함선을 인자로 안 받는다** - 잔해로 떨어져 나간 탄약고도 터지고, 그때는 위에
    /// Ship이 없다. 부모를 거슬러 찾아보고 없으면 중립으로 적는다.
    /// </summary>
    public static void Detonated(Component module)
    {
        if (module == null)
            return;

        Ship ship = module.GetComponentInParent<Ship>();

        Add(Kind.Detonated, module.name, ship != null ? ship.team : Ship.Team.Neutral);
    }

    /// <summary>
    /// 판 하나가 처음 뚫렸다.
    ///
    /// **내 배만 적는다.** 이 사건을 읽는 쪽이 함내 통신이라 적함 판이 뚫린 것은 승무원이
    /// 알 수도 없고 말할 일도 없다. 그리고 적함까지 적으면 한 전투에 수백 줄이 쌓이는데
    /// 그걸 읽는 소비자가 하나도 없다 - 엔딩 판정은 결과를 보지 판을 안 센다.
    ///
    /// 걸쇠는 <see cref="Armor.AnyBreached"/>에 이미 있다. 여기서 중복을 막지 않는다.
    /// </summary>
    public static void Penetrated(Armor plate)
    {
        if (plate == null)
            return;

        Ship ship = plate.GetComponentInParent<Ship>();

        if (ship == null || !ship.IsPlayerControlled)
            return;

        Add(Kind.Penetrated, ship.SectionName(plate.transform.localPosition), ship.team);
    }

    /// <summary>
    /// 기밀 구획이 우주로 열렸다. 방의 칸 평균으로 구역 이름을 뽑는다 - 방 하나가
    /// 함수와 함미에 걸치는 일은 없으므로 중심 하나면 충분하다.
    /// </summary>
    public static void RoomBreached(Ship ship, Room room)
    {
        if (ship == null || room == null || room.cells == null || room.cells.Count == 0
            || !ship.IsPlayerControlled || ship.Map == null)
            return;

        Vector2 sum = Vector2.zero;

        for (int i = 0; i < room.cells.Count; i++)
            sum += ship.Map.ToLocal(room.cells[i].x, room.cells[i].y);

        Add(Kind.RoomBreached, ship.SectionName(sum / room.cells.Count), ship.team);
    }

    /// <summary>
    /// 역할 하나가 끊긴 순간. 기관실이 진공이 되면 기관사는 영영 말이 없다.
    ///
    /// <see cref="Finished"/>와 같은 이유로 부르는 자리가 하나여야 한다 - 조건이
    /// 파생값(살아 있는 모듈 + 기압)이라 매 틱 읽으면 같은 상실을 60번 적는다.
    /// 다른 점은 되돌아오지 않는다는 것이다: <c>IsCombatEffective</c>는 원자로를 고치면
    /// 다시 참이 되지만 역할은 <see cref="Ship.CrewAlive"/>처럼 한 방향이다. 그래서
    /// 부르는 쪽의 걸쇠가 "지난 틱 값"이 아니라 "이미 잃었나"여야 한다.
    /// </summary>
    public static void RoleLost(Ship ship, Ship.ShipRole role)
    {
        if (ship != null)
            Add(Kind.RoleLost, role.ToString(), ship.team);
    }

    /// <summary>
    /// 구역 진입. 플레이어의 진행이라 팀은 Ally다. 죽어서 같은 구역을 다시 열어도
    /// 또 적는다 - "몇 번째 시도인가"가 그 자체로 대본이 읽을 수 있는 값이다.
    /// </summary>
    public static void SectorEntered(int number)
        => Add(Kind.SectorEntered, number.ToString(), Ship.Team.Ally);

    /// <summary>구역 승리. 부르는 자리는 Campaign.OnBattleEnd의 승리 가지 하나다.</summary>
    public static void SectorCleared(int number)
        => Add(Kind.SectorCleared, number.ToString(), Ship.Team.Ally);

    // ---- 상황 보고. 시뮬이 아는데 화면에 없던 것들. 전부 플레이어(Ally) 사건이다. ----

    /// <summary>탄약 25% 아래로 처음 내려갔다. <c>what</c>은 남은 발수. 걸쇠는 Ship이 든다.</summary>
    public static void AmmoLow(int left) => Add(Kind.AmmoLow, left.ToString(), Ship.Team.Ally);

    /// <summary>탄약 0. 포탑은 돌지만 안 나간다 - 왜 안 쏘는지 모르는 것이 제일 나쁘다.</summary>
    public static void AmmoOut() => Add(Kind.AmmoOut, "0", Ship.Team.Ally);

    /// <summary>급여가 들어왔다. <c>what</c>은 금액.</summary>
    public static void Paid(int credits) => Add(Kind.Paid, credits.ToString(), Ship.Team.Ally);

    /// <summary>보급 자리에서 실었다. <c>what</c>은 제일 많이 받은 것(MUN/PROP/CR).</summary>
    public static void Supplied(string what) => Add(Kind.Supplied, what, Ship.Team.Ally);

    private static string NameOf(Ship ship)
        => string.IsNullOrEmpty(ship.shipDefName) ? ship.name : ship.shipDefName;

    private static void Add(Kind kind, string what, Ship.Team team)
    {
        Entry entry = new(kind, what, team, Core.TickManager.currentTick);

        _entries.Add(entry);

        // **구독자의 사고가 시뮬레이션을 못 무너뜨리게 한다.** 이 자리들은 전부 동기 호출
        // 한가운데다 - Detonated는 Armor.Die -> CollapseRemains -> SpallResolver.Burst ->
        // Detonate 재진입 사슬 안에서 불린다. 대사창이 던지면 그 예외가 사슬을 거슬러 올라가
        // 폭발이 절반만 적용된 채로 끝난다: 어떤 판은 죽고 어떤 판은 손도 안 댄 상태.
        // 기록은 이미 남았으니 UI의 실패가 기록까지 지우지도 않는다.
        try
        {
            onEntry?.Invoke(entry);
        }
        catch (Exception e)
        {
            Debug.LogError($"[RunLog] {kind} 구독자가 던졌다: {e}");
        }
    }

    /// <summary>새 런. 함장이 죽으면 다음은 다른 함장이라 기록도 새로 시작한다.</summary>
    public static void Clear() => _entries.Clear();

    /// <summary>어떤 팀의 함선을 몇 척이나 끝냈나. 엔딩 조건이 읽을 첫 번째 질문이다.</summary>
    public static int FinishedCount(Ship.Team team) => Count(Kind.Finished, team);

    /// <summary>
    /// 이 종류의 사건이 지금까지 몇 번 있었나. **저장하지 않고 매번 센다** - 사건은
    /// 초당 몇 개가 아니라 전투당 몇 개라, 목록 순회가 캐시와 카운터를 유지하는 것보다
    /// 싸고 어긋날 자리가 없다.
    /// </summary>
    public static int Count(Kind kind)
    {
        int n = 0;

        for (int i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].kind == kind)
                n++;
        }

        return n;
    }

    /// <summary>팀까지 가른 횟수. "적함을 몇 척 끝냈나"가 이 모양이다.</summary>
    public static int Count(Kind kind, Ship.Team team)
    {
        int n = 0;

        for (int i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].kind == kind && _entries[i].team == team)
                n++;
        }

        return n;
    }
}
