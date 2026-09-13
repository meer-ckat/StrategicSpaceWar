using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 격파 X-ray의 자료. 죽을 때 한 번, 어느 판이 언제 죽었고 시타델이 어디였는지.
///
/// 워썬더 킬 카메라의 명시 목적이 "데미지 모델 교육"이다. 이 게임의 시뮬은 결정론인데
/// 플레이어 눈엔 운으로 보였다 - 시타델·후면·관통 여부를 모른 채 결과만 봐서. 죽음이
/// 아무것도 안 가르치면 로그라이크 학습 루프의 절반이 비어 있다.
///
/// **배가 사라진 뒤에도 그려야 한다.** 유폭 즉사는 GameObject가 먼저 없어지므로, 설계도와
/// 시타델은 살아 있는 동안 <see cref="Observe"/>가 들고 있고 죽은 판은 <see cref="PlateLost"/>가
/// 쌓는다. 그리는 쪽(ShipStatusHud)은 여기만 읽는다.
/// </summary>
public static class DeathXray
{
    public struct Lost
    {
        public Vector2Int cell;   // 설계도 칸
        public long tick;
    }

    public static ShipGrid.Map Design { get; private set; }
    public static readonly List<Vector2Int> Citadel = new();
    public static readonly List<Lost> LostPlates = new();
    public static long DownTick { get; private set; } = -1;
    public static string Cause { get; private set; } = "";

    /// <summary>
    /// 한 사건 = 같은 순간에 죽은 판 묶음. 유폭 한 방이 판 40장을 같은 틱에 죽이는데, 그걸 40개로
    /// 세면 "무엇이 먼저였나"가 그 40장에 묻힌다 - 관통 한 발이 판 하나를 죽이고, 그 다음 유폭이
    /// 40장을 죽인 것이 두 사건이다.
    /// </summary>
    public struct Group
    {
        public long tick;
        public List<Vector2Int> cells;
        public string tag;   // 그 순간의 RunLog 사건. "유폭" "절단" "관통". 없으면 ""
    }

    /// <summary>마지막 <see cref="MaxGroups"/>개. 0이 제일 오래된 것 - 재생 순서다.</summary>
    public static readonly List<Group> Groups = new();

    public const int MaxGroups = 10;

    /// <summary>이 틱 안이면 같은 사건. 유폭의 붕괴 연쇄가 몇 틱에 걸쳐 흐를 수 있다.</summary>
    private const int GroupTicks = 3;

    private static Ship _watched;

    /// <summary>매 프레임. 설계도가 바뀐 배(새 런, 새 구역 재건조)만 다시 읽는다 - 나머지 프레임은 참조 비교 하나.</summary>
    public static void Observe(Ship player)
    {
        if (player == null || player.DesignMap == null)
            return;

        if (player == _watched && player.DesignMap == Design)
            return;

        if (player != _watched)
            LostPlates.Clear();   // 다른 배다. 지난 배의 죽음을 물려주지 않는다

        _watched = player;
        Design = player.DesignMap;
        Citadel.Clear();

        foreach (CriticalModule m in player.shipCriticals)
        {
            if (m == null || !Ship.StillAboard(m, player))
                continue;

            Citadel.Add(Design.ToCell(player.transform.InverseTransformPoint(m.transform.position)));
        }
    }

    /// <summary>판이 죽었다. HullStructure.ReportPlateLost가 부른다 - 플레이어 배만 적는다.</summary>
    public static void PlateLost(Ship ship, Vector2Int designCell)
    {
        if (ship == null || ship != _watched)
            return;

        LostPlates.Add(new Lost { cell = designCell, tick = Core.TickManager.currentTick });
    }

    /// <summary>격파 확정. 원인 한 줄은 RunLog의 마지막 아군 사건에서 읽는다.</summary>
    public static void Capture()
    {
        DownTick = Core.TickManager.currentTick;
        Cause = "전투 불능";

        IReadOnlyList<RunLog.Entry> log = RunLog.Entries;

        BuildGroups(log);

        for (int i = log.Count - 1; i >= 0; i--)
        {
            RunLog.Entry e = log[i];

            if (e.team != Ship.Team.Ally)
                continue;

            switch (e.kind)
            {
                case RunLog.Kind.CrewLost: Cause = "승무원 전멸"; return;
                case RunLog.Kind.Detonated: Cause = e.what + " 유폭"; return;
                case RunLog.Kind.HullSplit: Cause = "선체 절단"; return;
                case RunLog.Kind.RoleLost: Cause = e.what + " 상실"; return;
            }
        }
    }

    private static void BuildGroups(IReadOnlyList<RunLog.Entry> log)
    {
        Groups.Clear();

        // 죽은 순서대로 쌓여 있다. 틱이 가까우면 같은 묶음.
        foreach (Lost l in LostPlates)
        {
            if (Groups.Count > 0 && l.tick - Groups[Groups.Count - 1].tick <= GroupTicks)
            {
                Groups[Groups.Count - 1].cells.Add(l.cell);
                continue;
            }

            Groups.Add(new Group { tick = l.tick, cells = new List<Vector2Int> { l.cell }, tag = "" });
        }

        if (Groups.Count > MaxGroups)
            Groups.RemoveRange(0, Groups.Count - MaxGroups);

        // 그 순간의 RunLog 사건을 묶음에 붙인다. 유폭이 같은 틱에 여러 모듈이어도 태그는 하나.
        for (int g = 0; g < Groups.Count; g++)
        {
            Group group = Groups[g];
            long from = group.tick - GroupTicks;
            long to = (g + 1 < Groups.Count ? Groups[g + 1].tick : long.MaxValue) - 1;

            bool det = false, split = false, pen = false, crew = false, role = false;

            for (int i = 0; i < log.Count; i++)
            {
                RunLog.Entry e = log[i];

                if (e.team != Ship.Team.Ally || e.tick < from || e.tick > to)
                    continue;

                switch (e.kind)
                {
                    case RunLog.Kind.Detonated: det = true; break;
                    case RunLog.Kind.HullSplit: split = true; break;
                    case RunLog.Kind.Penetrated: pen = true; break;
                    case RunLog.Kind.CrewLost: crew = true; break;
                    case RunLog.Kind.RoleLost: role = true; break;
                }
            }

            // 인과 순서로 잇는다. 관통이 탄약고를 때려 유폭이 나고 그것이 절단으로 - 한 순간에
            // 셋이 다 일어날 수 있고, "유폭"만 남기면 그 관통 한 발이 사라진다. 그 한 발이 원인이다.
            var tag = new System.Text.StringBuilder();
            if (pen) tag.Append("관통");
            if (det) tag.Append(tag.Length > 0 ? " → 유폭" : "유폭");
            if (split) tag.Append(tag.Length > 0 ? " → 절단" : "절단");
            if (crew) tag.Append(tag.Length > 0 ? " → 승무원" : "승무원");
            if (role) tag.Append(tag.Length > 0 ? " → 상실" : "상실");
            group.tag = tag.ToString();
            Groups[g] = group;
        }
    }

    public static void Reset()
    {
        Groups.Clear();
        _watched = null;
        Design = null;
        Citadel.Clear();
        LostPlates.Clear();
        DownTick = -1;
        Cause = "";
    }
}
