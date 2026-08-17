#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

/// <summary>
/// Tools > Defs > Run Def Tests.
///
/// 여기서 시험하는 것은 로더의 **거부 능력**이다. JsonUtility가 모르는 키를 조용히 버리기
/// 때문에, 오타 난 def를 걸러내지 못하면 이 시스템은 "설정한 줄 알았는데 기본값"을 양산한다.
/// 최상위 키 스캐너가 틀리면 그 방어선이 통째로 뚫린다.
/// </summary>
public static class ThingDefSelfTest
{
    private static int _pass;
    private static int _fail;

    [MenuItem("Tools/Defs/Run Def Tests")]
    public static void Run()
    {
        _pass = 0;
        _fail = 0;

        Keys("flat", "{\"a\":1,\"b\":2}", "a", "b");

        // 중첩된 객체와 배열의 키는 최상위가 아니다. 이걸 못 가르면 collider.size 같은 것이
        // "모르는 키"로 잡혀서 멀쩡한 def가 전부 거부된다.
        Keys("nested object", "{\"a\":1,\"col\":{\"size\":2},\"b\":3}", "a", "col", "b");
        Keys("array of objects", "{\"comps\":[{\"x\":1}],\"b\":2}", "comps", "b");

        // 값 문자열 안의 콜론. 이걸 놓치면 없는 키를 만들어내서 멀쩡한 def를 거부한다.
        Keys("colon inside a value", "{\"a\":\"x:y\",\"b\":2}", "a", "b");

        // 이스케이프된 따옴표. 문자열 끝을 잘못 잡으면 그 뒤가 통째로 어긋난다.
        Keys("escaped quote", "{\"a\":\"he said \\\"hi\\\"\",\"b\":2}", "a", "b");

        Keys("empty", "{}");

        // --- Validate ---
        Debug.Log("[ThingDef] 아래 두 케이스는 에러 로그가 나오는 것이 정상이다.");

        Check("오타 난 키는 def를 거부한다",
            !Validate("{\"defName\":\"t\",\"thingClass\":\"BallisticArmor\",\"rah\":500}"));

        Check("없는 thingClass는 def를 거부한다",
            !Validate("{\"defName\":\"t\",\"thingClass\":\"NoSuchClass\"}"));

        Check("헤더 키만 있는 def는 통과한다",
            Validate("{\"defName\":\"t\",\"thingClass\":\"BallisticArmor\"," +
                     "\"comps\":[],\"layer\":\"Armor\",\"collider\":{\"size\":{\"x\":1,\"y\":1}}}"));

        Check("주 컴포넌트의 필드는 통과한다",
            Validate("{\"defName\":\"t\",\"thingClass\":\"BallisticArmor\"," +
                     "\"rha\":500,\"hpPerSquareMetre\":300,\"plateThickness\":0.5}"));

        // comps에 있는 컴포넌트의 필드도 같은 파일에서 읽힌다. 이게 통과해야 장갑 수치와
        // 스킨 색이 한 파일에 나란히 있을 수 있다.
        Check("comps의 필드도 통과한다",
            Validate("{\"defName\":\"t\",\"thingClass\":\"BallisticArmor\"," +
                     "\"comps\":[\"ArmorSkin\"],\"rha\":500,\"erodeBelow\":0.6}"));

        Check("comps를 빠뜨리면 그 필드가 거부된다",
            !Validate("{\"defName\":\"t\",\"thingClass\":\"BallisticArmor\",\"erodeBelow\":0.6}"));

        // [FormerlySerializedAs]로 남긴 옛 이름. 이걸 모르면 아직 안 옮긴 def가 갑자기 깨진다.
        Check("옛 필드 이름도 통과한다",
            Validate("{\"defName\":\"t\",\"thingClass\":\"BallisticArmor\",\"cellHp\":300}"));

        // --- 배치만 갈아끼우기 ---
        // 함선 def에는 손으로 튜닝한 수치가 들어 있다. export가 배치를 다시 쓸 때 그걸
        // 지우면 조용히 배가 느려지거나 물러진다.
        Splice("배열 값 교체",
            "{\"a\":1,\"placements\":[1,2],\"b\":2}", "placements", "[9]",
            "{\"a\":1,\"placements\":[9],\"b\":2}");

        Splice("마지막 키여도 된다",
            "{\"a\":1,\"placements\":[1,2]}", "placements", "[9]",
            "{\"a\":1,\"placements\":[9]}");

        Splice("스칼라 값도 교체된다",
            "{\"a\":1,\"drag\":0.3,\"b\":2}", "drag", "0.9",
            "{\"a\":1,\"drag\":0.9,\"b\":2}");

        // 중첩 안의 같은 이름은 건드리면 안 된다
        Splice("중첩된 동명 키는 안 건드린다",
            "{\"x\":{\"drag\":1},\"drag\":2}", "drag", "9",
            "{\"x\":{\"drag\":1},\"drag\":9}");

        Check("없는 키는 null",
            DefKeys.ReplaceTopLevelValue("{\"a\":1}", "nope", "2") == null);

        // --- 클래스 이름 해석 ---
        // 짧은 이름이 되는 것이 요점이다. 이게 없으면 URP의 Light2D를 붙이려고 def에
        // UnityEngine.Rendering.Universal.Light2D를 통째로 적어야 한다.
        // 우리 어셈블리는 원래 Type.GetType 한 번에 잡힌다. 아래 짧은 이름 경로가 안 타는
        // 것이 정상이고, 이 줄은 그 빠른 길이 여전히 산다는 확인이다.
        Check("우리 어셈블리의 이름", DefKeys.Resolve("BallisticArmor") == typeof(BallisticArmor));

        Check("정규화 이름도 그대로 된다",
            DefKeys.Resolve("UnityEngine.SpriteRenderer") == typeof(SpriteRenderer));

        Check("남의 어셈블리도 짧은 이름으로", DefKeys.Resolve("SpriteRenderer") == typeof(SpriteRenderer));

        // Component가 아닌 것은 애초에 답이 될 수 없다. 이 필터가 없으면 짧은 이름의
        // 후보가 수천 개로 늘어 애매함 거부가 상시로 터진다.
        Check("Component가 아니면 안 찾는다", DefKeys.Resolve("Vector2") == null);

        // 점이 있는데 못 찾았으면 진짜 없는 것이다. 뒷부분만 떼어 비슷한 걸 집어오면
        // 오타가 다른 클래스로 조용히 성공한다 - def 검증이 막으려는 바로 그 실패다.
        Check("정규화 이름 오타는 짧은 이름으로 안 새어나간다",
            DefKeys.Resolve("Nope.Nada.SpriteRenderer") == null);

        Check("없는 이름은 null", DefKeys.Resolve("NoSuchComponentAnywhere") == null);

        // --- 실제 파일 ---
        DefDatabase.Reload();

        foreach (string name in new[]
                 {
                     "Armor mk5", "Lance Armor", "Ballistic Door", "Glass", "Rock", "Mirror",
                     "m7", "SuperDuper Engine", "45mm", "50.BMG", "Railgun Bullet",
                     "Magazine", "Reactor", "Blast Flash", "Blast Flash Large", "Cut Glow",
                 })
            Check($"실제 def '{name}'을 읽었다", DefDatabase.Has(name));

        // row는 아래로 증가한다. 이 한 줄이 뒤집히면 경사판과 거울의 rot·offset이 전부
        // 거울상이 되는데, 배는 여전히 지어지고 방도 멀쩡해서 눈으로만 틀린다.
        ShipGrid.Map probeMap = ShipGrid.Map.Centred(3, 3);

        Check("row가 증가하면 배 좌표계 y는 내려간다",
            probeMap.ToLocal(0, 1).y < probeMap.ToLocal(0, 0).y);

        // 배치 offset은 칸 좌표계다. 판이 돌아 있어도 "위로 1"은 위로 1이어야 한다.
        // box.offset은 회전 **뒤의** 로컬 좌표계라 Spawn이 -rot으로 돌려 넣는다. 이게 없으면
        // 접선으로 세운 거울 판이 반지름 대신 원 둘레로 미끄러지고, 45도 경사판은 √2/2씩 샌다.
        ThingDef mirror = DefDatabase.Get("Mirror");

        if (mirror != null)
        {
            Thing probe = mirror.Spawn(null, Vector2.zero, 90f, Vector2.one, new Vector2(0f, 1f));
            BoxCollider2D box = probe != null ? probe.GetComponent<BoxCollider2D>() : null;

            Check("배치 offset은 칸 좌표계다 (rot 90 -> 로컬 +x)",
                box != null && (box.offset - new Vector2(1f, 0f)).sqrMagnitude < 1e-6f);

            if (probe != null)
                Object.DestroyImmediate(probe.gameObject);
        }

        // 함선 def도 같은 검증을 탄다. 배 수치가 Ship이 아는 필드여야 통과한다.
        //
        // 배치가 부르는 def가 실제로 있는지도 여기서 본다. def 이름은 문자열이라 (JSON끼리는
        // GUID가 없다) def 파일 하나를 지우면 그걸 부르던 배치는 조용히 빈 칸이 된다 -
        // 배는 뜨는데 판만 없다. Armor mk5 slope를 지우고 Placement.size로 옮길 때 실제로
        // 이 자리가 필요했다.
        foreach (string name in new[]
                 { "destroyer", "frigate", "scout", "cruiser", "lance", "asteroid", "mirror", "derelict" })
        {
            ShipDef ship = ShipDef.Load(name);

            Check($"실제 함선 def '{name}'을 읽었다", ship != null);

            if (ship == null)
                continue;

            foreach (Placement p in ship.placements)
                if (!DefDatabase.Has(p.def))
                {
                    Check($"함선 '{name}'의 배치가 없는 def '{p.def}'을 부른다", false);
                    break;
                }
        }

        Debug.Log($"[ThingDef] {_pass} passed, {_fail} failed.");
    }

    private static void Splice(string name, string json, string key, string value, string want)
    {
        string got = DefKeys.ReplaceTopLevelValue(json, key, value);
        Check($"배치 교체: {name} (got {got ?? "null"})", got == want);
    }

    private static bool Validate(string json)
    {
        var def = JsonUtility.FromJson<ThingDef>(json);
        def.raw = json;
        def.source = "selftest";

        return def.Validate();
    }

    private static void Keys(string name, string json, params string[] expected)
    {
        List<string> got = ThingDef.TopLevelKeys(json);
        bool ok = got.Count == expected.Length;

        for (int i = 0; ok && i < expected.Length; i++)
            ok = got[i] == expected[i];

        Check($"키 스캔: {name} (got [{string.Join(",", got)}])", ok);
    }

    private static void Check(string name, bool ok)
    {
        if (ok)
        {
            _pass++;
            return;
        }

        _fail++;
        Debug.LogError($"[ThingDef] FAIL {name}");
    }
}
#endif
