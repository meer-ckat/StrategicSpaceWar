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
    public Thing Spawn(
        Transform parent,
        Vector2 localPosition,
        float rotationZ,
        Vector2 sizeOverride = default,
        Vector2 offsetShift = default)
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
        // 배치가 크기를 말했으면 그것이 이긴다. 격자는 콜라이더를 안 보므로 같은 def가
        // 자리마다 다른 크기로 서도 방·선체·파단은 아무것도 안 달라진다 - 경사장갑을 위해
        // def를 한 벌 더 두지 않아도 되는 이유가 이 분리다. 자세한 것은 Placement.size.
        Vector2 size = sizeOverride.x > 0f && sizeOverride.y > 0f ? sizeOverride : collider.size;

        if (size.x > 0f && size.y > 0f)
        {
            BoxCollider2D box = go.AddComponent<BoxCollider2D>();
            box.size = size;

            // **배치의 offset은 칸 좌표계다. box.offset은 회전 뒤의 로컬 좌표계다.** 그냥
            // 더하면 판이 기울어져 있을 때 엉뚱한 방향으로 민다 - 45도면 √2/2씩 새고,
            // 접선 방향으로 세운 거울 판이면 반지름으로 밀라고 한 것이 원 둘레로 미끄러진다.
            //
            // def의 collider.offset은 def 자기 기하라 로컬이 맞다. 배치가 준 것만 되돌린다.
            box.offset = collider.offset + Ballistics.Rotate(offsetShift, -rotationZ);
        }
        else if (offsetShift != Vector2.zero)
        {
            // 콜라이더가 없으면 밀 것이 없다. 조용히 버리면 증상이 "안 움직이는데?"뿐이고
            // 콘솔에는 아무것도 없다 - 이 프로젝트에서 제일 비싼 실패 유형이다.
            Debug.LogWarning(
                $"[ThingDef] {source}: 배치가 offset {offsetShift}을 줬는데 콜라이더가 없어서 " +
                "버린다. offset은 오브젝트가 아니라 콜라이더를 미는 값이다.");
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

        _mainType = DefKeys.Resolve(thingClass);

        if (_mainType == null || !typeof(Thing).IsAssignableFrom(_mainType))
        {
            Debug.LogError(
                $"[ThingDef] {source}: thingClass '{thingClass}'를 못 찾았거나 Thing이 아니다.");
            return false;
        }

        _compTypes = new Type[comps.Length];

        for (int i = 0; i < comps.Length; i++)
        {
            _compTypes[i] = DefKeys.Resolve(comps[i]);

            if (_compTypes[i] == null || !typeof(Component).IsAssignableFrom(_compTypes[i]))
            {
                Debug.LogError($"[ThingDef] {source}: comp '{comps[i]}'를 못 찾았다.");
                return false;
            }
        }

        var targets = new Type[_compTypes.Length + 1];
        targets[0] = _mainType;
        _compTypes.CopyTo(targets, 1);

        return !DefKeys.HasUnknown(raw, source, HeaderKeys, targets);
    }

    private static readonly string[] HeaderKeys =
        { "defName", "thingClass", "comps", "layer", "collider" };

    /// <summary>self-test용. 스캐너 자체는 DefKeys가 들고 있다.</summary>
    public static List<string> TopLevelKeys(string json) => DefKeys.TopLevel(json);
}
