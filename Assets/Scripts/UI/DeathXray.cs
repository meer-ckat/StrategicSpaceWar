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

    public static void Reset()
    {
        _watched = null;
        Design = null;
        Citadel.Clear();
        LostPlates.Clear();
        DownTick = -1;
        Cause = "";
    }
}
