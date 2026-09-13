using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 최근 피격을 **설계도 칸 단위로** 적는다. 판정에는 영향을 주지 않고 UI가 읽기만 한다.
///
/// 월드 좌표를 안 든다. 표시가 월드가 아니라 AIRFRAME 패널(화면 고정 선체 그림) 위에서
/// 일어나므로, 배가 움직여도 따라갈 것이 없다 - anchor·Transform 추적이 통째로 필요 없다.
///
/// 시간 정책도 없다. 상한(Capacity) FIFO로만 유계이고 "언제까지 보여줄지"는 읽는 쪽이
/// 각자 정한다.
/// </summary>
public static class DamageLog
{
    /// <summary>이 시간 안의 명중만 한 칸으로 이어 센다. 넘으면 새 줄 - "이번 연사에 몇 발".</summary>
    public const float MergeWindow = 0.3f;

    private const int Capacity = 24;

    public struct ArmorMark
    {
        /// <summary>맞은 배. 어느 패널에 그릴지는 읽는 쪽이 이걸로 고른다.</summary>
        public Ship ship;

        /// <summary>설계도 격자의 칸. AIRFRAME이 그리는 좌표계와 같다.</summary>
        public Vector2Int cell;

        public HitOutcome outcome;
        public float time;

        /// <summary>
        /// 충각으로 갈린 칸인가. **HitOutcome에 Ram을 안 넣는 이유** - 저 열거형은
        /// PenetrationManager가 내는 탄도 판정이고, 충각은 관통도 도탄도 아니다.
        /// 표시 색과 수명만 갈라지면 되므로 여기 플래그 하나로 충분하다.
        /// </summary>
        public bool ram;
    }

    public static readonly List<ArmorMark> Armors = new();

    /// <summary>
    /// 판정 하나가 확정됐을 때 부른다. 같은 배·같은 칸·MergeWindow 안이면 한 줄로 이어 센다.
    ///
    /// **충돌하면 제일 심한 판정이 이긴다.** HUD가 답할 질문은 "이 0.3초 동안 이 칸에서
    /// 최악이 무엇이었나"이고, 정확한 재현은 HitReadout(구석 로그)이 맡는다.
    /// HitOutcome의 선언 순서(Ricochet &lt; Blocked &lt; Penetrated)가 그대로 심각도라
    /// 표를 따로 두지 않는다.
    /// </summary>
    public static void Hit(Armor armor, HitOutcome outcome) => Mark(armor, outcome, false);

    /// <summary>
    /// 충각이 판을 갈았다. **매 틱 들어온다** - RamImpact.Punch가 접촉 중 계속 돌기
    /// 때문이다. 병합이 그걸 그대로 받아 time만 갱신하므로 접촉하는 동안 그 칸이 계속
    /// 켜져 있고, 떨어지면 꺼진다 - 지속 접촉에 지속 표시라 오히려 맞는 그림이다.
    /// </summary>
    public static void Ram(Armor armor) => Mark(armor, HitOutcome.Blocked, true);

    private static void Mark(Armor armor, HitOutcome outcome, bool ram)
    {
        if (armor == null)
            return;

        Ship ship = armor.GetComponentInParent<Ship>();

        // 잔해로 떨어져 나간 판은 배가 없다 - 그릴 패널도 없으니 여기서 걸러진다.
        if (ship == null || ship.DesignMap == null)
            return;

        Vector2Int cell = ship.DesignMap.ToCell(armor.transform.localPosition);
        float now = Time.time;

        // 충각은 탄이 아니라 SpallTrails에 선이 없다. X-ray가 "왜 이 판이 죽었나"에 답하려면
        // 갈린 자리라도 찍혀 있어야 한다 - 여기가 충각 피해의 단일 깔때기다.
        if (ram && ship.IsPlayerControlled)
            DeathXray.AddRam(armor.transform.position);

        for (int i = 0; i < Armors.Count; i++)
        {
            ArmorMark mark = Armors[i];

            if (mark.ship != ship || mark.cell != cell || now - mark.time > MergeWindow)
                continue;

            // 충각과 피탄이 같은 칸에서 겹치면 피탄이 이긴다 - 충각은 접촉하는 동안
            // 계속 오므로 안 그러면 그 칸의 명중 판정이 영영 안 보인다.
            if (!ram && mark.ram)
            {
                mark.ram = false;
                mark.outcome = outcome;
            }
            else if (ram == mark.ram && outcome > mark.outcome)
            {
                mark.outcome = outcome;
            }

            mark.time = now;

            Armors[i] = mark;
            return;
        }

        // 오래된 것부터 밀려난다. 순서(오래된 것이 앞)를 읽는 쪽이 전제한다.
        if (Armors.Count >= Capacity)
            Armors.RemoveAt(0);

        Armors.Add(new ArmorMark
        {
            ship = ship,
            cell = cell,
            outcome = outcome,
            time = now,
            ram = ram,
        });
    }
}
