using UnityEngine;

/// <summary>
/// 충각 진입점. 실제 계산은 RamImpact에 있다 - 잔해와 운석(Hulk)도 같은 규칙으로 맞아야
/// 해서, 함선에만 있으면 잔해가 체력 무한인 벽이 된다.
///
/// **충돌 콜백이 없다.** 매 틱 앞을 쓸어서 부술 수 있는 것을 Simulate 전에 없앤다.
/// 그래서 솔버가 그 판들을 볼 일이 아예 없고, 되돌릴 것도 꺾임 프레임도 없다.
/// 못 부순 판에 막히는 것은 솔버가 알아서 하고, 그 정지는 옳은 정지다.
/// </summary>
public partial class Ship
{
    /// <summary>지난 틱에 Drive가 건 추력(N, 월드). 밀어서 깨는 몫이 여기서 나온다.</summary>
    private Vector2 _thrust;

    /// <summary>OnTick 앞쪽에서 부른다. SplitIfBroken과 같은 자리, 같은 이유.</summary>
    private void Ram()
    {
        RamImpact.Punch(transform, rig, isDriverReady ? _thrust : Vector2.zero);
    }
}
