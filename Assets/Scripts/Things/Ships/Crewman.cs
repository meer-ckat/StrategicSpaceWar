using UnityEngine;

/// <summary>승무원 한 명. Ship이 소유하는 순수 데이터라 MonoBehaviour가 아니다.</summary>
public class Crewman
{
    // 방 참조가 아니라 칸인 이유: 파단으로 BuildRooms가 돌면 Room이 전부 새 객체다.
    // 맵 칸도 아니고 ShipGrid.Anchor(로컬 정수 좌표)다 - 격자가 밀려도 같은 자리다.
    public Vector2Int anchor;
    public bool alive = true;
}
