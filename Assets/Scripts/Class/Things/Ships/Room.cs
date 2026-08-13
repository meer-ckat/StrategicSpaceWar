using System.Collections.Generic;
using UnityEngine;

/// <summary>기밀 구획 하나. Ship이 소유하는 순수 데이터라 MonoBehaviour가 아니다.</summary>
public class Room
{
    public List<Vector2Int> cells;
    public List<Armor> walls = new();
    public List<Door> doors = new();
    public float air;               // 절대량. 1칸당 1이면 1기압.

    /// <summary>
    /// 맵이 이 방에 둘러주기로 한 판의 수. walls에 살아 있는 판이 이보다 적으면
    /// 그만큼 우주로 뚫려 있다 - 부서졌든 선체째 떨어져 나갔든 세는 법이 같다.
    /// </summary>
    public int boundaryPlates;

    public Room(List<Vector2Int> cells)
    {
        this.cells = cells;
        air = Volume;
    }

    public float Volume => cells.Count;
    public float Pressure => Volume > 0f ? air / Volume : 0f;
}
