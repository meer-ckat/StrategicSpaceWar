using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// defName -> 그 물건을 만드는 법.
///
/// 원본은 StreamingAssets/Defs/*.json 하나뿐이다. 파일 하나가 def 하나고, 프리팹이 하던 일을
/// 전부 데이터가 한다 - 어느 클래스를 붙일지, 어떤 부속을 같이 다는지, 콜라이더가 얼마인지,
/// 수치가 얼마인지.
///
/// Resources는 안 쓴다. def끼리는 GUID가 없으므로 서로를 **이름으로만** 안다 - 포탑 def가
/// 탄 def를 부르는 것도 그렇다. 그게 모딩이 열리는 지점이다.
/// </summary>
public static class DefDatabase
{
    /// <summary>StreamingAssets 아래. 여기 있는 .json 전부가 def다.</summary>
    public const string DefFolder = "Defs";

    private static Dictionary<string, ThingDef> _defs;

    public static string DefDirectory => Path.Combine(Application.streamingAssetsPath, DefFolder);

    public static bool Has(string defName)
    {
        Load();

        return !string.IsNullOrEmpty(defName) && _defs.ContainsKey(defName);
    }

    /// <summary>
    /// 물건 하나를 만들어 parent 밑 그 자리에 놓는다. 없으면 null이고, 부르는 쪽이 시끄럽게
    /// 실패할 책임을 진다.
    ///
    /// 위치와 회전을 여기서 받는 이유: def로 만든 물건은 전부 붙이고 자리를 잡은 **뒤에**
    /// 활성화돼야 한다. 밖에서 나중에 옮기면 Awake가 이미 지나간 뒤가 된다.
    /// </summary>
    public static Thing Spawn(string defName, Transform parent, Vector2 localPosition, float rotationZ)
    {
        Load();

        if (string.IsNullOrEmpty(defName))
            return null;

        return _defs.TryGetValue(defName, out ThingDef def)
            ? def.Spawn(parent, localPosition, rotationZ)
            : null;
    }

    /// <summary>
    /// 부모 없이 월드 좌표에 하나 놓는다. 탄이 이 경우다 - 배의 자식이 아니라 씬에 그냥 뜬다.
    ///
    /// 부모가 없으면 로컬 좌표가 곧 월드 좌표라, 자리를 잡고 활성화하는 순서가 그대로 지켜진다.
    /// </summary>
    public static Thing SpawnAt(string defName, Vector2 worldPosition, Quaternion rotation)
        => Spawn(defName, null, worldPosition, rotation.eulerAngles.z);

    private static void Load()
    {
        if (_defs != null)
            return;

        Reload();
    }

    public static void Reload()
    {
        _defs = new Dictionary<string, ThingDef>();
        LoadDefs();

        Debug.Log($"[DefDatabase] def {_defs.Count}개.");
    }

    // ponytail: File 직접 읽기. ShipDef와 같은 이유로 데스크톱 전용이다.
    private static void LoadDefs()
    {
        if (!Directory.Exists(DefDirectory))
            return;

        foreach (string path in Directory.GetFiles(DefDirectory, "*.json", SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(path);
            var def = JsonUtility.FromJson<ThingDef>(text);

            if (def == null)
            {
                Debug.LogError($"[DefDatabase] {Path.GetFileName(path)}를 읽지 못했다.");
                continue;
            }

            def.raw = text;
            def.source = Path.GetFileName(path);

            // 검증에 걸린 def는 아예 안 싣는다. 절반만 반영된 물건이 조용히 돌아다니는 것보다
            // "그 이름이 없다"고 시끄럽게 실패하는 쪽이 훨씬 빨리 잡힌다.
            if (!def.Validate())
                continue;

            if (_defs.ContainsKey(def.defName))
            {
                Debug.LogError($"[DefDatabase] defName '{def.defName}'이 둘 이상이다: {def.source}");
                continue;
            }

            _defs[def.defName] = def;
        }
    }

    /// <summary>
    /// 한 오브젝트에 Thing이 둘 이상 붙어 있을 수 있다 - 문은 Armor이면서 Door다. 이름을 든
    /// 쪽이 답이고, GetComponent 하나로 뽑으면 컴포넌트 순서 운에 맡기게 된다.
    /// </summary>
    public static Thing NameOf(GameObject go)
    {
        Thing[] things = go.GetComponents<Thing>();

        if (things.Length == 0)
            return null;

        foreach (Thing t in things)
        {
            if (!string.IsNullOrEmpty(t.defName))
                return t;
        }

        return things[0];
    }

#if UNITY_EDITOR
    [UnityEditor.MenuItem("Tools/Defs/Reload")]
    private static void ReloadMenu()
    {
        Reload();
        Debug.Log("[DefDatabase] 다시 읽었다. 플레이 중이면 배를 다시 지어야 반영된다.");
    }

    [UnityEditor.MenuItem("Tools/Defs/Open Def Folder")]
    private static void OpenFolder() => UnityEditor.EditorUtility.RevealInFinder(DefDirectory);
#endif
}
