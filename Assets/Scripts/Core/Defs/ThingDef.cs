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
    public bool sealsRoom = true;

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
    /// `thingClass`가 가리키는 실제 C# 타입. <see cref="Validate"/>가 한 번 풀어 둔 것이다.
    ///
    /// **타입만 내주고 판단은 밖에서 한다.** 여기에 `IsPlate` 같은 것을 두면 def 층이
    /// `Armor`를 알아야 하고, 그러면 "물건 한 종류의 정의"가 함선 격자의 규칙까지 들고
    /// 있게 된다. 지금은 def가 <see cref="Spawn"/>할 줄만 알면 된다.
    /// </summary>
    public Type MainType => _mainType;

    /// <summary>
    /// 물건 하나가 태어나는 값. **판 한 장마다 지난다** - destroyer가 629칸, lance가
    /// 306칸이라 배 한 척의 소환 비용은 사실상 이 마커의 합이다. 지금은 그 대부분이
    /// Instantiate 하나라 <see cref="_mProto"/>는 def당 한 번만 뜬다.
    /// </summary>
    private static readonly Unity.Profiling.ProfilerMarker _mSpawn = new("ThingDef.Spawn");
    private static readonly Unity.Profiling.ProfilerMarker _mProto = new("ThingDef.Prototype");

    /// <summary>
    /// 이 def의 원본 한 벌. 비활성이고 씬에 안 보이며 절대 켜지지 않는다.
    ///
    /// **여기 있는 것이 def 해석의 전부다.** 리플렉션으로 컴포넌트를 붙이고
    /// <see cref="JsonUtility.FromJsonOverwrite"/>로 원문을 붓는 일이 def당 한 번만
    /// 일어난다. 예전에는 판 한 장마다 일어났고, destroyer가 629칸이라 배 한 척을
    /// 짓는 것이 곧 JSON을 629번 파싱하는 것이었다 - 구역 진입의 스파이크가 그것이다.
    /// </summary>
    private GameObject _prototype;

    private GameObject Prototype()
    {
        if (_prototype != null)
            return _prototype;

        using var _ = _mProto.Auto();

        var go = new GameObject(defName);

        // **절대 켜지 않는다.** 켜는 순간 Awake가 도는데 원본은 스탯만 든 껍데기다.
        go.SetActive(false);
        go.hideFlags = HideFlags.HideAndDontSave;

        if (!string.IsNullOrEmpty(layer))
        {
            int id = LayerMask.NameToLayer(layer);

            if (id < 0)
                Debug.LogError($"[ThingDef] {source}: '{layer}'라는 레이어가 없다. Default로 둔다.");
            else
                go.layer = id;
        }

        // 크기 0은 "콜라이더 없음"이다. 탄은 레이캐스트로 판정하므로 콜라이더가 없어야 하고,
        // 무조건 붙이면 탄끼리 부딪히기 시작한다. 배치가 크기를 주는 경우는 복제 뒤에 붙인다.
        if (collider.size.x > 0f && collider.size.y > 0f)
        {
            BoxCollider2D box = go.AddComponent<BoxCollider2D>();
            box.size = collider.size;
            box.offset = collider.offset;
        }

        // 그림은 전부 절차적이다. 스프라이트 자산이 없고, ArmorSkin 같은 부속이 콜라이더
        // 모양대로 런타임에 텍스처를 굽는다. 머티리얼은 URP 스톡 기본값 그대로.
        go.AddComponent<SpriteRenderer>();

        JsonUtility.FromJsonOverwrite(raw, go.AddComponent(_mainType));

        foreach (Type comp in _compTypes)
            JsonUtility.FromJsonOverwrite(raw, go.AddComponent(comp));

        _prototype = go;
        return go;
    }

    /// <summary>
    /// 원본을 버린다. def를 다시 읽을 때 <see cref="DefDatabase.Reload"/>가 부른다 -
    /// 안 부르면 옛 수치를 든 원본이 씬에 계속 떠 있고, 새로 지은 배가 그것을 복제한다.
    /// </summary>
    public void DiscardPrototype()
    {
        if (_prototype == null)
            return;

        if (Application.isPlaying)
            UnityEngine.Object.Destroy(_prototype);
        else
            UnityEngine.Object.DestroyImmediate(_prototype);

        _prototype = null;
    }

    /// <summary>
    /// 이 def대로 물건 하나를 만들어 parent 밑에 놓는다.
    ///
    /// **비활성으로 만들고 마지막에 켠다.** 원본이 비활성이라 복제도 비활성으로 태어나고,
    /// 그래서 stats가 들어가기 전에 Armor.Awake가 도는 일이 없다. 콜라이더·모양·자리까지
    /// 다 잡은 뒤에 한 번에 켜는 것이 유일하게 안전한 순서다.
    /// </summary>
    public Thing Spawn(
        Transform parent,
        Vector2 localPosition,
        float rotationZ,
        Vector2 sizeOverride = default,
        Vector2 offsetShift = default,
        Vector2[] shapeOverride = null)
    {
        if (_mainType == null)
            return null;

        using var _ = _mSpawn.Auto();

        // 비활성 원본의 복제도 비활성이다. **비활성으로 만들고 마지막에 켠다**는 규칙이
        // 여기서도 그대로 성립한다 - 콜라이더도 모양도 자리도 다 잡은 뒤에 Awake가 돈다.
        GameObject go = (GameObject)GameObject.Instantiate(
            Prototype(), parent, instantiateInWorldSpace: false);

        // 복제는 원본의 hideFlags까지 물려받는다. 안 지우면 판이 계층에서 사라지고
        // 씬에 저장도 안 된다 - 증상이 "배는 도는데 하이어라키가 비었다"라 한참 헤맨다.
        go.hideFlags = HideFlags.None;
        go.name = defName;

        go.transform.localPosition = localPosition;
        go.transform.localRotation = Quaternion.Euler(0f, 0f, rotationZ);

        // 배치가 크기를 말했으면 그것이 이긴다. 격자는 콜라이더를 안 보므로 같은 def가
        // 자리마다 다른 크기로 서도 방·선체·파단은 아무것도 안 달라진다 - 경사장갑을 위해
        // def를 한 벌 더 두지 않아도 되는 이유가 이 분리다. 자세한 것은 Placement.size.
        Vector2 size = sizeOverride.x > 0f && sizeOverride.y > 0f ? sizeOverride : collider.size;

        if (size.x > 0f && size.y > 0f)
        {
            // def가 콜라이더를 안 줬는데 배치가 크기를 준 경우에만 새로 붙는다.
            if (!go.TryGetComponent(out BoxCollider2D box))
                box = go.AddComponent<BoxCollider2D>();

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

        var thing = (Thing)go.GetComponent(_mainType);

        // **켜기 전.** 뒤면 Awake의 BakeShape가 이미 사각형 기준으로 구운 뒤다.
        // 배치가 준 모양이 def의 모양을 이긴다. Placement.size와 같은 규약이다.
        if (shapeOverride != null && shapeOverride.Length >= 3 && thing is Armor armour)
            armour.PrepareShape(shapeOverride);

        // 켜기가 실패하면 소환도 실패다. 이유는 Thing.Activate에 - 반쯤 지어진 채
        // 비활성으로 남는 유령을 막는 자리다. 부르는 쪽은 이미 null을 다룬다
        // (Gun.Fire의 `shell == null`).
        return Thing.Activate(go) ? thing : null;
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
