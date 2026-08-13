using UnityEngine;
using Core;

/// <summary>
/// 함선에서 떨어져 나온 선체 조각. 장갑판이 그대로 붙어 있으므로 계속 맞고 계속 부서진다 -
/// 관통 코드 입장에서는 그냥 리지드바디가 다른 판일 뿐이다.
/// </summary>
[RequireComponent(typeof(Rigidbody2D))]
public class HullDebris : Thing
{
    /// <summary>영원히 떠다니게 두면 한 판이 끝날 때쯤 씬이 잔해로 덮인다.</summary>
    public int lifeTick = 3600;   // 60 s

    public override void OnTick()
    {
        // 판이 하나도 안 남았으면 빈 리지드바디만 떠다니는 셈이다
        if (GetComponentInChildren<Armor>() == null)
        {
            Destroy(gameObject);
            return;
        }

        if (TickManager.currentTick - spawnTick >= lifeTick)
            Destroy(gameObject);
    }
}
