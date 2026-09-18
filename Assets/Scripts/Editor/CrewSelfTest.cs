#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// Tools > Ship > Run Crew Tests. Suffocate/PlaceCrew가 순수 함수라 플레이 모드가 필요 없다.
public static class CrewSelfTest
{
    private static int _pass;
    private static int _fail;

    [MenuItem("Tools/Ship/Run Crew Tests")]
    public static void Run()
    {
        _pass = 0;
        _fail = 0;

        // 큰 방(6칸) | 작은 방(2칸)
        var map = ShipGrid.ParseMap(string.Join("\n",
            "#######",
            "#...#.#",
            "#...#.#",
            "#######"));
        var rooms = ShipGrid.BuildRooms(map, null, null);
        Check(rooms.Count == 2, "방 둘");

        Room big = rooms[0].Volume > rooms[1].Volume ? rooms[0] : rooms[1];
        Room small = big == rooms[0] ? rooms[1] : rooms[0];

        // 1) 3명 → 큰 방 2, 작은 방 1
        var crew = Ship.PlaceCrew(rooms, map, 3);
        Check(crew.Count == 3, "3명 앉혔다");
        int inBig = 0;
        foreach (Crewman c in crew)
            if (c.anchor == ShipGrid.Anchor(map, big.cells[0])) inBig++;
        Check(inBig == 2, $"큰 방에 2명 (got {inBig})");

        // 2) 작은 방만 비우면 1명 죽고 배는 산다
        small.air = 0f;
        Ship.Suffocate(crew, rooms, map);
        int alive = 0;
        foreach (Crewman c in crew) if (c.alive) alive++;
        Check(alive == 2, $"작은 방 진공: 2명 생존 (got {alive})");

        // 3) 재가압해도 안 돌아온다
        small.air = small.Volume;
        Ship.Suffocate(crew, rooms, map);
        alive = 0;
        foreach (Crewman c in crew) if (c.alive) alive++;
        Check(alive == 2, "재가압해도 죽은 사람은 그대로");

        // 4) 큰 방까지 비우면 전멸
        big.air = 0f;
        Ship.Suffocate(crew, rooms, map);
        alive = 0;
        foreach (Crewman c in crew) if (c.alive) alive++;
        Check(alive == 0, "전멸");

        // 5) 방이 없어진 사람(칸이 잔해로 떠났다)도 죽는다
        var lone = new List<Crewman> { new Crewman { anchor = new Vector2Int(999, 999) } };
        Ship.Suffocate(lone, rooms, map);
        Check(!lone[0].alive, "방 밖 = 사망");

        // 6) 방 없는 배는 승무원 0
        Check(Ship.PlaceCrew(new List<Room>(), map, 3).Count == 0, "방 없으면 0명");

        Debug.Log($"[Crew] {_pass}개 통과, {_fail}개 실패.");
    }

    private static void Check(bool ok, string what)
    {
        if (ok) { _pass++; return; }
        _fail++;
        Debug.LogError($"[Crew] 실패: {what}");
    }
}
#endif
