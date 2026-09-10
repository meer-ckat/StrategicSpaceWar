#if UNITY_EDITOR
using System.Collections.Generic;
using IMGUI;
using UnityEditor;
using UnityEngine;

// 물류창의 순수 함수만. 모션·키보드·소리·통신 카드는 플레이에서 본다.
public static class LogisticsSelfTest
{
    private static int _pass;
    private static int _fail;

    [MenuItem("Tools/UI/Run Logistics Tests")]
    public static void Run()
    {
        _pass = 0;
        _fail = 0;

        // ---------------------------------------------------------
        // 1) Ships / Facilities / Badge - 적만, 함급별, 등장 순서, 시설은 따로
        // ---------------------------------------------------------
        SectorDef mixed = Sector(
            Spawn("frigate", "Enemy"),
            Spawn("scout", "Ally"),
            Spawn("destroyer", "enemy"),        // 대소문자 무시 (SpawnDef.Side)
            Spawn("frigate", "Enemy"),
            Spawn("mirror", "Enemy", hulk: true),
            Spawn("asteroid", "Neutral", hulk: true));

        List<(string ship, int n)> ships = LogisticsScreen.Ships(mixed);
        Check(ships.Count == 2 && ships[0].ship == "frigate" && ships[0].n == 2, "같은 함급을 묶고 등장 순서를 지킨다");
        Check(ships[1].ship == "destroyer" && ships[1].n == 1, "소문자 team도 적이다");
        Check(LogisticsScreen.Facilities(mixed) == 1, "적 hulk만 시설로 센다");

        string badge = LogisticsScreen.Badge(mixed);
        Check(badge.Contains("HOSTILE ×3"), "배지는 적 함선 총수");
        Check(badge.Contains("TARGET ×1"), "배지에 시설");
        Check(!badge.Contains("REFIT") && !badge.Contains("+"), "정비·자재 없으면 안 적는다");

        SectorDef depot = Sector(Spawn("asteroid", "Neutral", hulk: true));
        depot.refit = true;
        depot.materials = 6;
        Check(LogisticsScreen.Ships(depot).Count == 0 && LogisticsScreen.Facilities(depot) == 0, "중립 hulk만 있으면 접촉 없음");
        Check(LogisticsScreen.Badge(depot).Contains("REFIT") && LogisticsScreen.Badge(depot).Contains("+6"), "정비·자재 배지");
        Check(LogisticsScreen.Badge(Sector()) == "CLEAR", "아무것도 없으면 CLEAR");

        // ---------------------------------------------------------
        // 2) NodePos - 같은 입력이면 같은 자리, 칸 안에, 갈래 칸끼리 안 겹침
        // ---------------------------------------------------------
        Rect view = new Rect(100f, 50f, 400f, 600f);
        float contentW = 380f;
        Vector2 a = LogisticsScreen.NodePos(7, view, contentW, 3, 0, 1, 5, 0);
        Vector2 b = LogisticsScreen.NodePos(7, view, contentW, 3, 0, 1, 5, 0);
        Check(a == b, "결정론");

        bool inRange = true, apart = true;
        for (int key = 0; key < 50; key++)
        {
            Vector2 p = LogisticsScreen.NodePos(7, view, contentW, 0, 0, 1, key, 0);
            inRange &= p.x >= view.x - 0.01f && p.x + LogisticsScreen.NodeSize.x <= view.x + contentW + 0.01f;

            Vector2 l = LogisticsScreen.NodePos(7, view, contentW, 0, 0, 2, key, 1);
            Vector2 r = LogisticsScreen.NodePos(7, view, contentW, 0, 1, 2, key, 2);
            apart &= l.x + LogisticsScreen.NodeSize.x <= r.x + 0.01f;
        }
        Check(inRange, "x가 내용 폭 안에 있다");
        Check(apart, "두 갈래 칸이 안 겹친다");

        // ---------------------------------------------------------
        // 3) Branch - 유효 갈래 0이면 선 0, 2면 줄기+막대+가지 2
        // ---------------------------------------------------------
        var g = new GUIGroup(GUIContent.none, view, "test");
        GUIBoxLabel[] stubs = LogisticsScreen.Branch(g, new SectorDef[] { null, null }, new Vector2[2], new Vector2(200f, 300f), null);
        Check(g.Childrens.Count == 0, "갈래가 없으면 선을 안 그린다");
        Check(stubs.Length == 2 && stubs[0] == null && stubs[1] == null, "가지도 없다");

        SectorDef s = new SectorDef();
        stubs = LogisticsScreen.Branch(g, new[] { s, s }, new[] { new Vector2(120f, 100f), new Vector2(320f, 100f) }, new Vector2(200f, 300f), null);
        Check(g.Childrens.Count == 4, "줄기 1 + 막대 1 + 가지 2");
        Check(stubs[0] != null && stubs[1] != null, "가지 둘을 돌려준다");
        GUIManager.Unregister(g);

        // ---------------------------------------------------------
        // 4) Comms - 파일이 있으면 조건 넷이 전부 줄을 가진다
        // ---------------------------------------------------------
        LogisticsScreen.Comms comms = LogisticsScreen.Comms.Load();
        foreach (string when in new[] { "noservice", "refit", "hostile", "salvage", "clear" })
            Check(comms.Find(when) != null, $"comms.json에 '{when}' 줄이 있다");

        Debug.Log($"[LogisticsSelfTest] {_pass} pass / {_fail} fail");
    }

    private static SectorDef Sector(params SpawnDef[] spawns)
    {
        var s = new SectorDef();
        s.spawns.AddRange(spawns);
        return s;
    }

    private static SpawnDef Spawn(string ship, string team, bool hulk = false) =>
        new SpawnDef { ship = ship, team = team, hulk = hulk };

    private static void Check(bool ok, string what)
    {
        if (ok) { _pass++; return; }
        _fail++;
        Debug.LogError($"[LogisticsSelfTest] FAIL: {what}");
    }
}
#endif
