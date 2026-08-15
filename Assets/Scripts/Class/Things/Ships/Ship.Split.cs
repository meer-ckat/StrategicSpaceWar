using UnityEngine;

/// <summary>
/// 선체 파단의 함선 쪽 절반. 실제 BFS와 이탈 처리는 HullStructure에 있다 - 잔해도 같은
/// 규칙으로 다시 쪼개져야 해서, 함선에만 두면 규칙이 두 벌이 된다.
///
/// 여기 남은 것은 함선에만 있는 후속 처리뿐이다: 떨어져 나간 엔진이 계속 추력을 내면
/// 안 되고, 사라진 벽이 있는 방은 뚫린 것이다.
/// </summary>
public partial class Ship
{
    /// <summary>떨어져 나가는 조각이 받는 이탈 속도. 붙어 있던 자리에서 밀려나는 만큼.</summary>
    [Header("선체 파단")]
    public float breakawaySpeed = 2f;

    private HullStructure _structure;

    /// <summary>OnTick 맨 앞에서 부른다. 물리 콜백 밖이라 재부모화가 안전하다.</summary>
    private void SplitIfBroken()
    {
        if (_structure == null || !_structure.SplitIfBroken())
            return;

        shipArmors.Clear();
        shipEngines.Clear();
        shipGuns.Clear();
        shipCriticals.Clear();
        shipArmors.AddRange(GetComponentsInChildren<Armor>());
        shipEngines.AddRange(GetComponentsInChildren<Engine>());
        shipGuns.AddRange(GetComponentsInChildren<Gun>());
        shipCriticals.AddRange(GetComponentsInChildren<CriticalModule>());

        BuildRooms();
    }
}
