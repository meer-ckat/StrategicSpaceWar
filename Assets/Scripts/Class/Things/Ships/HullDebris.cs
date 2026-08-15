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

    private HullStructure _structure;

    protected override void Awake()
    {
        base.Awake();
        _structure = GetComponent<HullStructure>();
    }

    public override void OnTick()
    {
        // 함선과 같은 자리, 같은 이유. 물리 콜백 밖에서, 이번 틱의 무엇보다 먼저.
        // 잔해도 다시 쪼개진다 - 가운데 판이 없어지면 남은 두 조각은 더 이상 한 몸이 아니다.
        _structure?.SplitIfBroken();

        // 판이 하나도 안 남았으면 빈 리지드바디만 떠다니는 셈이다
        if (GetComponentInChildren<Armor>() == null)
        {
            Destroy(gameObject);
            return;
        }

        if (TickManager.currentTick - spawnTick >= lifeTick)
            Destroy(gameObject);
    }

    /// <summary>
    /// 잔해도 받히면 부서진다. 이게 없으면 잔해가 체력 무한인 벽이라, 들이받은 함선만
    /// 일방적으로 갈려나간다.
    /// </summary>
    private void OnCollisionEnter2D(Collision2D collision)
        => RamImpact.Resolve(transform, collision);
}
