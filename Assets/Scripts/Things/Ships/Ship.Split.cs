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
        if (_structure == null || !_structure.TrySplitIfBroken())
            return;

        // **List 오버로드다.** 배열을 돌려주는 쪽은 호출마다 새 배열을 만들고, 판 300장짜리
        // 배가 갈라지는 틱에 네 번 나가면 그것만으로 GC.Collect가 프레임에 걸린다 -
        // 파단은 연쇄로 오므로 그 틱이 제일 바쁜 틱이다. 이 오버로드는 목록을 비우고 채운다.
        GetComponentsInChildren(shipArmors);
        GetComponentsInChildren(shipEngines);
        GetComponentsInChildren(shipGuns);
        GetComponentsInChildren(shipCriticals);

        BuildRooms();

        // **목록을 다시 채우고 방을 다시 지은 뒤**에 알린다. TrySplitIfBroken 직후에 부르면
        // 구독자가 보는 shipGuns·shipCriticals에 방금 잔해로 떠난 부품이 그대로 들어 있어서,
        // IsCombatEffective를 읽는 쪽이 두 동강 난 배를 아직 멀쩡하다고 본다.
        //
        // 여기가 "갈라졌다"의 유일한 자리다. TrySplitIfBroken이 실제로 조각을 떼어냈을 때만
        // true를 돌려주므로, 판이 죽을 때마다가 아니라 배가 갈라진 틱에만 적힌다.
        RunLog.HullSplit(this);
    }
}
