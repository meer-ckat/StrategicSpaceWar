using System.Text;
using UnityEngine;

/// <summary>
/// 들판 한 판의 판정 지표. **재는 것만 하고 게임을 하나도 안 바꾼다.**
///
/// `Docs/OpenSector-Routes.md` §8이 지표 다섯을 적어 놓고 첫 단계를 "프로토타입으로 플레이"라 했는데,
/// 지표가 없으면 "선택이 너무 쉽게 끝나는지"를 눈으로 판단하게 된다. 그 판단이 다음 단계를 정하므로
/// 눈이 아니라 숫자여야 한다 - CLAUDE.md "측정이 과제의 일부다"가 이 자리를 말한다.
///
/// 다섯 지표와 여기서 그것을 어떻게 근사하는가:
///
/// 1. **정보 얻으러 직선을 벗어난 비율** - 실제 이동 거리 / 도착점에서 고른 출구까지의 직선 거리.
///    1.0이면 곧장 갔고, 1.5면 절반을 더 돌았다.
/// 2. **정보 확인 후 출구를 바꾼 비율** - 자료가 열린 순간 향하던 출구와 실제로 나간 출구가 다른가.
///    "향하던 출구"는 그 순간 속도가 가장 많이 좁히던 쪽이다 - 의도를 묻는 입력이 없으니 이것이 제일 정직한 근사다.
/// 3. **부족 자원과 항로의 상관** - 출항 때 지갑 네 칸을 그대로 적는다. 어느 것이 마른지는 여기서 안 고른다(Wallet 참고).
/// 4. **노획 목표에 따라 조준이 달라졌나** - 부순 적함의 탱크·탄약고 중 **성한 채로 남은 비율**.
///    보존 사격을 했으면 이 값이 오른다. 조준점을 직접 못 보므로 결과로 잰다.
/// 5. **특정 항로 선택률** - 고른 갈래를 적는다. 70% 편중은 여러 판을 모아 본다.
///
/// 출력은 파일 한 줄이다(`CruiseBench`와 같은 길). 한 판이 한 줄이라 표로 붙여 보면 그대로 표본이 된다.
/// </summary>
public static class FieldLog
{
    /// <summary>이 판을 재고 있나. 들판이 아니면 꺼진다.</summary>
    public static bool Running { get; private set; }

    private static Vector2 _entry, _last;
    private static float _path;
    private static int _headingAtIntel = -1;
    private static bool _intelSeen;
    private static string _sector;
    private static int _ticks;

    /// <summary>들판에 들어섰다. Campaign.Enter가 부른다.</summary>
    public static void Begin(string sector, Vector2 at)
    {
        Running = true;
        _sector = sector;
        _entry = _last = at;
        _path = 0f;
        _ticks = 0;
        _intelSeen = false;
        _headingAtIntel = -1;
    }

    /// <summary>
    /// 매 틱. 이동 거리를 쌓고, 자료가 열리는 **순간**의 향하던 출구를 한 번 붙잡는다.
    /// 그 순간을 놓치면 2번 지표가 통째로 사라진다 - 나중에는 이미 자료를 본 뒤의 행동뿐이다.
    /// </summary>
    public static void Sample(Campaign campaign, Ship player)
    {
        if (!Running || campaign == null || player == null)
            return;

        Vector2 at = player.transform.position;
        _path += Vector2.Distance(_last, at);
        _last = at;
        _ticks++;

        if (_intelSeen || !campaign.RouteIntelUnlocked)
            return;

        _intelSeen = true;
        _headingAtIntel = Closing(campaign, player);
    }

    /// <summary>속도가 가장 많이 좁히고 있는 출구. 어디로도 안 좁히면 -1.</summary>
    private static int Closing(Campaign campaign, Ship player)
    {
        Vector2 at = player.transform.position;
        int best = -1;
        float bestRate = 0f;

        for (int k = 0; k < campaign.GateCount; k++)
        {
            Vector2 d = campaign.GateAt(k) - at;

            if (d.sqrMagnitude < 1f)
                continue;

            float rate = Vector2.Dot(player.velocity, d.normalized);

            if (rate > bestRate)
            {
                bestRate = rate;
                best = k;
            }
        }

        return best;
    }

    /// <summary>출항했다. 한 줄 적고 끈다. Campaign.ReachGate가 부른다.</summary>
    public static void End(Campaign campaign, Ship player, int lane)
    {
        if (!Running)
            return;

        Running = false;

        float straight = campaign != null && lane >= 0 && lane < campaign.GateCount
            ? Vector2.Distance(_entry, campaign.GateAt(lane))
            : 0f;

        float detour = straight > 1f ? _path / straight : 0f;
        bool switched = _intelSeen && _headingAtIntel >= 0 && _headingAtIntel != lane;

        Intact(out int tanks, out int tanksAlive, out int mags, out int magsAlive);

        var line = new StringBuilder();
        line.Append($"{System.DateTime.Now:MM-dd HH:mm}\t{_sector}\t갈래 {lane}\t");
        line.Append($"우회 {detour:0.00}\t거리 {_path / 1000f:0.0}km\t시간 {_ticks / 60f:0}s\t");
        line.Append($"자료 {(_intelSeen ? "봄" : "안봄")}\t출구변경 {(switched ? "예" : "아니오")}\t");
        line.Append($"{Wallet(player)}\t열기 {RunState.Heat}\t");
        line.Append($"탱크보존 {tanksAlive}/{tanks}\t탄약고보존 {magsAlive}/{mags}");

        Debug.Log($"[FieldLog] {line}");

#if UNITY_EDITOR
        string path = System.IO.Path.Combine(Application.dataPath, "../FieldLog.tsv");
        System.IO.File.AppendAllText(path, line + "\n");
#endif
    }

    /// <summary>
    /// 출항 시점의 지갑. **어느 것이 제일 말랐는지 여기서 고르지 않는다** - 창고 상한 셋이 아직 추정치라
    /// (MTRL 300 / MUN 1,200 / PROP 3,000,000) 상한 대비 비율을 서로 비교할 근거가 없다. 한쪽 상한만
    /// 잘못 잡아도 "부족한 자원"이 통째로 바뀌고, 그 값으로 3번 지표(부족 자원과 항로의 상관)를 읽으면
    /// 상한의 오차를 플레이어의 판단으로 착각하게 된다. 네 칸을 그대로 적고 판단은 표를 모아서 한다.
    /// </summary>
    private static string Wallet(Ship player)
    {
        string ammo = player != null && player.MaxRounds > 0
            ? $"{player.Rounds}/{player.MaxRounds}"
            : "-";

        return $"CR {RunState.Credits}\tMUN {RunState.Munitions}\tPROP {RunState.Propellant}\t탄약 {ammo}";
    }

    /// <summary>
    /// 부순 적함에서 성한 채로 남은 탱크·탄약고. **전투력을 잃은 배만 센다** - 안 건드린 자리의
    /// 멀쩡한 배까지 세면 "보존을 잘했다"가 "안 싸웠다"와 구별이 안 된다(ComputeSalvage와 같은 필터).
    /// </summary>
    private static void Intact(out int tanks, out int tanksAlive, out int mags, out int magsAlive)
    {
        tanks = tanksAlive = mags = magsAlive = 0;

        for (int i = 0; i < Ship.All.Count; i++)
        {
            Ship s = Ship.All[i];

            if (s == null || s.team != Ship.Team.Enemy || s.IsCombatEffective)
                continue;

            foreach (Tank tank in s.GetComponentsInChildren<Tank>())
            {
                tanks++;

                if (!tank.Neutralized)
                    tanksAlive++;
            }

            foreach (CriticalModule module in s.GetComponentsInChildren<CriticalModule>())
            {
                if (module.maxRounds <= 0)
                    continue;

                mags++;

                if (!module.Neutralized)
                    magsAlive++;
            }
        }
    }
}
