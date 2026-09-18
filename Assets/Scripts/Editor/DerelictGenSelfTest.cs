#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// 난파선 생성기. 플레이 모드도 씬도 필요 없다 - Crush가 배치 리스트만 다루고
// StampFromDef가 오브젝트를 안 만든다.
public static class DerelictGenSelfTest
{
    private static int _pass;
    private static int _fail;

    [MenuItem("Tools/Ship/Run Derelict Tests")]
    public static void Run()
    {
        _pass = 0;
        _fail = 0;

        ShipDef source = ShipDef.Load("destroyer");

        if (source == null)
        {
            Debug.LogError("[DerelictGen] destroyer.json을 못 읽어서 테스트를 못 돌린다.");
            return;
        }

        // ---------------------------------------------------------
        // 1) 부순 배는 원본보다 작고, 배는 남아 있다
        // ---------------------------------------------------------
        ShipDef wreck = DerelictGen.Crush(source, 0, "test~wreck0");

        Check(wreck != null, "destroyer를 부수면 난파선이 나온다");

        if (wreck == null)
        {
            Report();
            return;
        }

        Check(wreck.placements.Count < source.placements.Count, "판이 줄었다");
        Check(wreck.placements.Count >= 12, "배라고 부를 만큼은 남았다");
        Check(wreck.hullSkin == "" && wreck.basedOn == "", "그림과 basedOn을 버렸다");
        Check(wreck.defName == "test~wreck0", "이름이 붙었다");

        // ---------------------------------------------------------
        // 2) 체력은 전부 [DropBelow, 1] 안이고, 실제로 상해 있다
        // ---------------------------------------------------------
        bool inRange = true;
        int hurt = 0;

        foreach (Placement p in wreck.placements)
        {
            if (p.hp < DerelictGen.DropBelow || p.hp > 1f)
                inRange = false;

            if (p.hp < 0.999f)
                hurt++;
        }

        Check(inRange, "남은 판의 hp가 전부 [DropBelow, 1]");
        Check(hurt > wreck.placements.Count / 2, "절반 넘게 상해 있다 (펄린 부식이 먹었다)");

        // ---------------------------------------------------------
        // 3) 원본을 안 건드렸다 - 캐시가 같은 객체를 돌려주므로 여기가 갈리면
        //    멀쩡한 배가 상한 채로 태어난다
        // ---------------------------------------------------------
        bool sourceClean = true;

        foreach (Placement p in source.placements)
        {
            if (p.hp < 0.999f)
                sourceClean = false;
        }

        Check(sourceClean, "설계도의 hp는 그대로 1이다");

        // ---------------------------------------------------------
        // 4) 결정론 - 같은 원본 + 같은 번호면 글자 하나까지 같다
        // ---------------------------------------------------------
        ShipDef again = DerelictGen.Crush(source, 0, "test~wreck0");

        Check(again != null && Same(wreck.placements, again.placements), "같은 씨앗은 같은 난파선");

        ShipDef other = DerelictGen.Crush(source, 1, "test~wreck1");

        Check(other != null && !Same(wreck.placements, other.placements), "변종 번호가 다르면 다른 난파선");

        // ---------------------------------------------------------
        // 5) 격자로 다시 읽힌다 - 후면·방·파단이 전부 이 맵에서 나오므로
        //    여기서 null이면 소환은 되고 안이 비는 배가 나온다
        // ---------------------------------------------------------
        ShipGrid.Map map = ShipBuilder.StampFromDef(wreck);

        Check(map != null, "난파선도 격자를 찍는다");

        if (map != null)
        {
            RectInt box = wreck.Bbox();

            Check(map.width == box.width && map.height == box.height,
                "격자 크기가 잘라낸 bbox와 같다 (basedOn이 비어 있다)");
        }

        // ---------------------------------------------------------
        // 6) 센티널만 바뀐다
        // ---------------------------------------------------------
        var rng = new DeterministicRng(1234);

        Check(DerelictGen.PickName("asteroid", ref rng) == "asteroid", "운석은 운석으로 남는다");

        var rng2 = new DeterministicRng(1234);
        string picked = DerelictGen.PickName(DerelictGen.Sentinel, ref rng2);

        Check(picked != DerelictGen.Sentinel || DerelictGen.Names.Count == 0,
            "derelict는 생성된 난파선으로 바뀐다");

        Check(DerelictGen.Names.Count > 0, $"난파선 목록이 비지 않았다 ({DerelictGen.Names.Count}척)");

        Report();
    }

    private static bool Same(List<Placement> a, List<Placement> b)
    {
        if (a.Count != b.Count)
            return false;

        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].def != b[i].def || a[i].col != b[i].col || a[i].row != b[i].row
                || !Mathf.Approximately(a[i].hp, b[i].hp))
                return false;
        }

        return true;
    }

    private static void Check(bool ok, string what)
    {
        if (ok)
        {
            _pass++;
            return;
        }

        _fail++;
        Debug.LogError($"[DerelictGen] 실패: {what}");
    }

    private static void Report()
        => Debug.Log($"[DerelictGen] {_pass}개 통과, {_fail}개 실패.");
}
#endif
