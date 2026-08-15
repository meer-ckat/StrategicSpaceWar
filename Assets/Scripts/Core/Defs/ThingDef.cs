using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Serialization;

/// <summary>
/// 물건 한 종류의 정의 전부. 프리팹이 하던 일을 데이터가 한다.
///
/// **파일 하나 = def 하나이고, 파일 전체가 컴포넌트에 통째로 부어진다.**
/// <see cref="JsonUtility.FromJsonOverwrite"/>가 자기가 아는 필드만 집어가고 나머지는 무시하므로,
/// 컴포넌트에 새 [SerializeField]를 하나 추가하면 그 순간부터 JSON에서 설정 가능해진다 -
/// 로더를 고칠 필요가 없다. 필드마다 파싱 코드를 쓰는 시스템은 반드시 뒤처진다.
///
/// 대가는 <see cref="Validate"/>다. FromJsonOverwrite는 모르는 키를 **조용히 버린다.**
/// rha를 rah로 오타 내면 에러 없이 기본값이 들어가고, 증상은 "장갑이 좀 약한 것 같은데"다.
/// 그래서 로더가 리플렉션으로 필드 이름을 뽑아 대조하고, 모르는 키가 있으면 def를 거부한다.
/// 이 검증은 옵션이 아니라 이 설계의 절반이다.
/// </summary>
[Serializable]
public class ThingDef
{
    /// <summary>부르는 이름. 배 JSON이 이걸로 물건을 지목한다.</summary>
    public string defName;

    /// <summary>붙일 주 컴포넌트의 C# 클래스 이름. RimWorld의 thingClass와 같은 자리.</summary>
    public string thingClass;

    /// <summary>같이 붙일 부속 컴포넌트들. 이것들도 같은 파일에서 자기 필드를 집어간다.</summary>
    public string[] comps = Array.Empty<string>();

    /// <summary>레이어 **이름**. 번호는 프로젝트 설정을 건드리면 밀리지만 이름은 안 밀린다.</summary>
    public string layer;

    public ColliderDef collider = new();

    [Serializable]
    public class ColliderDef
    {
        /// <summary>0이면 콜라이더를 안 붙인다. 탄처럼 레이캐스트로만 판정하는 것들이 그렇다.</summary>
        public Vector2 size = Vector2.one;
        public Vector2 offset = Vector2.zero;
    }

    /// <summary>파일 원문. 컴포넌트마다 이걸 통째로 붓는다.</summary>
    [NonSerialized] public string raw;

    /// <summary>어디서 읽었는지. 에러 메시지가 파일을 짚어줘야 고칠 수 있다.</summary>
    [NonSerialized] public string source;

    private Type _mainType;
    private Type[] _compTypes;

    /// <summary>
    /// 이 def대로 물건 하나를 만들어 parent 밑에 놓는다.
    ///
    /// **비활성으로 만들고 마지막에 켠다.** AddComponent는 오브젝트가 활성이면 Awake를 즉시
    /// 부르는데, 그러면 stats가 들어가기 전에 Armor.Awake가 돌아서 판이 기본값 체력으로
    /// 태어난다. 위치까지 다 잡은 뒤에 한 번에 켜는 것이 유일하게 안전한 순서다.
    /// </summary>
    public Thing Spawn(Transform parent, Vector2 localPosition, float rotationZ)
    {
        if (_mainType == null)
            return null;

        var go = new GameObject(defName);
        go.SetActive(false);

        go.transform.SetParent(parent, worldPositionStays: false);
        go.transform.localPosition = localPosition;
        go.transform.localRotation = Quaternion.Euler(0f, 0f, rotationZ);

        if (!string.IsNullOrEmpty(layer))
        {
            int id = LayerMask.NameToLayer(layer);

            if (id < 0)
                Debug.LogError($"[ThingDef] {source}: '{layer}'라는 레이어가 없다. Default로 둔다.");
            else
                go.layer = id;
        }

        // 크기 0은 "콜라이더 없음"이다. 탄은 레이캐스트로 판정하므로 콜라이더가 없어야 하고,
        // 무조건 붙이면 탄끼리 부딪히기 시작한다.
        //
        // 있을 때는 컴포넌트보다 먼저 와야 한다. Armor.Awake가 이걸 읽어 서브셀 격자와
        // 체력을 정하는데, 없으면 fallbackCellSize로 조용히 새어 나간다.
        if (collider.size.x > 0f && collider.size.y > 0f)
        {
            BoxCollider2D box = go.AddComponent<BoxCollider2D>();
            box.size = collider.size;
            box.offset = collider.offset;
        }

        // 그림은 전부 절차적이다. 스프라이트 자산이 없고, ArmorSkin 같은 부속이 콜라이더
        // 모양대로 런타임에 텍스처를 굽는다. 머티리얼은 URP 스톡 기본값 그대로.
        go.AddComponent<SpriteRenderer>();

        var thing = (Thing)go.AddComponent(_mainType);
        JsonUtility.FromJsonOverwrite(raw, thing);

        foreach (Type comp in _compTypes)
            JsonUtility.FromJsonOverwrite(raw, go.AddComponent(comp));

        go.SetActive(true);
        return thing;
    }

    /// <summary>
    /// 클래스를 찾고, JSON의 최상위 키를 전부 아는지 확인한다. 하나라도 모르면 false -
    /// 부분적으로 반영된 def는 없느니만 못하다.
    /// </summary>
    public bool Validate()
    {
        if (string.IsNullOrEmpty(defName))
        {
            Debug.LogError($"[ThingDef] {source}: defName이 없다.");
            return false;
        }

        _mainType = ResolveType(thingClass);

        if (_mainType == null || !typeof(Thing).IsAssignableFrom(_mainType))
        {
            Debug.LogError(
                $"[ThingDef] {source}: thingClass '{thingClass}'를 못 찾았거나 Thing이 아니다.");
            return false;
        }

        _compTypes = new Type[comps.Length];

        for (int i = 0; i < comps.Length; i++)
        {
            _compTypes[i] = ResolveType(comps[i]);

            if (_compTypes[i] == null || !typeof(Component).IsAssignableFrom(_compTypes[i]))
            {
                Debug.LogError($"[ThingDef] {source}: comp '{comps[i]}'를 못 찾았다.");
                return false;
            }
        }

        var known = new HashSet<string>(HeaderKeys);
        CollectSerialisedFields(_mainType, known);

        foreach (Type comp in _compTypes)
            CollectSerialisedFields(comp, known);

        var unknown = new List<string>();

        foreach (string key in TopLevelKeys(raw))
        {
            if (!known.Contains(key))
                unknown.Add(key);
        }

        if (unknown.Count == 0)
            return true;

        Debug.LogError(
            $"[ThingDef] {source}: 아무도 모르는 키 {string.Join(", ", unknown)}. " +
            "오타이거나 컴포넌트를 빠뜨린 것이다 - 그냥 두면 조용히 기본값으로 묻힌다.");

        return false;
    }

    private static readonly string[] HeaderKeys =
        { "defName", "thingClass", "comps", "layer", "collider" };

    /// <summary>
    /// 컴포넌트가 실제로 직렬화하는 필드 이름. public 필드와 [SerializeField]가 붙은 private
    /// 필드, 그리고 [FormerlySerializedAs]의 옛 이름까지. MonoBehaviour 위쪽은 안 본다 -
    /// 거기 것들은 def가 건드릴 물건이 아니다.
    /// </summary>
    private static void CollectSerialisedFields(Type type, HashSet<string> into)
    {
        const BindingFlags Flags = BindingFlags.Instance
                                 | BindingFlags.Public
                                 | BindingFlags.NonPublic
                                 | BindingFlags.DeclaredOnly;

        for (Type cur = type; cur != null && cur != typeof(MonoBehaviour); cur = cur.BaseType)
        {
            foreach (FieldInfo field in cur.GetFields(Flags))
            {
                if (field.IsNotSerialized)
                    continue;

                if (!field.IsPublic && field.GetCustomAttribute<SerializeField>() == null)
                    continue;

                into.Add(field.Name);

                foreach (FormerlySerializedAsAttribute old in
                         field.GetCustomAttributes<FormerlySerializedAsAttribute>())
                    into.Add(old.oldName);
            }
        }
    }

    /// <summary>
    /// JSON 최상위 객체의 키만 뽑는다. JsonUtility는 자기가 아는 필드만 채우고 나머지는
    /// 말없이 버리므로, 원문을 직접 훑는 것 말고는 오타를 잡을 방법이 없다.
    /// </summary>
    public static List<string> TopLevelKeys(string json)
    {
        var keys = new List<string>();

        if (string.IsNullOrEmpty(json))
            return keys;

        int depth = 0;
        int stringStart = -1;
        bool inString = false, escaped = false;
        string pending = null;

        for (int i = 0; i < json.Length; i++)
        {
            char c = json[i];

            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"')
                {
                    inString = false;

                    // 깊이 1의 문자열만 키 후보다. 값이면 다음 글자가 ':'가 아니라 흘러간다.
                    if (depth == 1)
                        pending = json.Substring(stringStart, i - stringStart);
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    stringStart = i + 1;
                    break;

                case ':':
                    if (depth == 1 && pending != null)
                    {
                        keys.Add(pending);
                        pending = null;
                    }
                    break;

                case '{':
                case '[':
                    depth++;
                    pending = null;
                    break;

                case '}':
                case ']':
                    depth--;
                    pending = null;
                    break;
            }
        }

        return keys;
    }

    private static readonly Dictionary<string, Type> _typeCache = new();

    private static Type ResolveType(string name)
    {
        if (string.IsNullOrEmpty(name))
            return null;

        if (_typeCache.TryGetValue(name, out Type cached))
            return cached;

        // 같은 어셈블리(Assembly-CSharp)면 이것으로 끝난다. 나중에 asmdef로 갈라도
        // 아래 스캔이 받아주므로 def 파일을 고칠 일이 없다.
        Type found = Type.GetType(name);

        if (found == null)
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                found = asm.GetType(name);

                if (found != null)
                    break;
            }
        }

        _typeCache[name] = found;
        return found;
    }
}
