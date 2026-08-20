using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 수리. **구역 사이에만 일어나는 일이라 틱 루프에 없다.**
///
/// 고치는 것은 상한 판뿐이고 없어진 판은 못 되살린다. 판이 통째로 사라지는 것은 격자가
/// 바뀌는 일이라 방·구조 장부를 다시 지어야 하고, 그건 수리가 아니라 재건이다.
/// </summary>
public partial class Ship
{
    // 구역마다 한 번 도는 일이지만, 판 300장짜리 배에서 매번 리스트를 새로 만들 이유는 없다.
    private static readonly List<Armor> _repairQueue = new();

    /// <summary>
    /// 노획 <paramref name="budget"/>장어치로 상한 판을 고친다. 실제로 쓴 장수를 돌려준다.
    ///
    /// **제일 많이 상한 것부터.** 한 장을 어디 쓰든 값이 같으니, 제일 약해진 판을 되살리는
    /// 것이 같은 값으로 제일 많이 사는 길이다. 판 하나에 노획 한 장이고 부분 수리는 없다 -
    /// "0.6까지 고쳤다"는 상태는 화면에도 안 보이고 다음 전투에서 의미도 없다.
    ///
    /// <see cref="Armor.RestoreHealthFraction"/>이 값을 그냥 놓는다는 것이 중요하다. 피해
    /// 경로로 되돌리면 서브셀이 실제로 죽어서, 고치려던 판이 PlateCollapseFraction을 넘겨
    /// 무너진다.
    /// </summary>
    public int RepairPlates(int budget)
    {
        if (budget <= 0)
            return 0;

        _repairQueue.Clear();

        foreach (Armor plate in shipArmors)
        {
            // 잔해로 떠난 판은 목록에 그대로 살아 있다. 그걸 고치면 100 m 뒤에 떠 있는
            // 남의 판에 노획을 쓴다 - 엔진과 포탑이 겪은 것과 같은 함정이다.
            if (plate != null && StillAboard(plate, this) && plate.HealthFraction < 1f)
                _repairQueue.Add(plate);
        }

        if (_repairQueue.Count == 0)
            return 0;

        _repairQueue.Sort((a, b) => a.HealthFraction.CompareTo(b.HealthFraction));

        int used = Mathf.Min(budget, _repairQueue.Count);

        for (int i = 0; i < used; i++)
            _repairQueue[i].RestoreHealthFraction(1f);

        return used;
    }

    /// <summary>고칠 것이 몇 장 남았는가. 노획을 남기지 않으려고 부르는 쪽이 미리 본다.</summary>
    public int DamagedPlateCount()
    {
        int count = 0;

        foreach (Armor plate in shipArmors)
        {
            if (plate != null && StillAboard(plate, this) && plate.HealthFraction < 1f)
                count++;
        }

        return count;
    }
}
