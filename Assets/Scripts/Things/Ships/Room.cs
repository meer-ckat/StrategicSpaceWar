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

    /// <summary>
    /// 마지막으로 센 파공 수와, 그때의 <see cref="Ship.BreachVersion"/>.
    ///
    /// 파공 수는 판이 뚫리거나·죽거나·후면이 날아갈 때만 바뀌는데, 세는 비용은 방의
    /// 벽 수 + 칸 수라 안 캐시하면 아무 일도 안 일어나는 틱에도 배마다 수백 번 돈다.
    /// -1로 시작하는 것이 요점이다 - 버전이 0에서 시작하므로 기본값 0이면 "이미 셌다"가
    /// 되어 갓 지어진 방이 파공 0으로 굳는다.
    /// </summary>
    public int breaches;
    public int breachVersion = -1;

    public Room(List<Vector2Int> cells)
    {
        this.cells = cells;
        air = Volume;
    }

    public float Volume => cells.Count;
    public float Pressure => Volume > 0f ? air / Volume : 0f;
}
