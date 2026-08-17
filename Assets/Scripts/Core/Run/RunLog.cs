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
    public static int FinishedCount(Ship.Team team)
    {
        int n = 0;

        for (int i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].kind == Kind.Finished && _entries[i].team == team)
                n++;
        }

        return n;
    }
}
