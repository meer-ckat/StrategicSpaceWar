using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Serialization;

/// <summary>
/// def 파일의 최상위 키가 전부 누군가에게 읽히는지 확인한다.
///
/// 이게 필요한 이유는 <see cref="JsonUtility.FromJsonOverwrite"/>가 **모르는 키를 조용히
/// 버리기** 때문이다. rha를 rah로 오타 내면 에러 없이 기본값이 들어가고, 증상은 "장갑이 좀
/// 약한 것 같은데"다. 이 프로젝트에서 제일 비싼 실패 유형이고, 데이터 주도 설계의 대가다.
///
/// ThingDef(부품)와 ShipDef(함선)가 같이 쓴다. 둘 다 "파일 전체를 컴포넌트에 붓는다"는
/// 같은 트릭 위에 있으므로, 방어선도 하나여야 한다.
/// </summary>
public static class DefKeys
{
    /// <summary>
    /// 모르는 키가 하나라도 있으면 true를 돌려주고 무엇인지 말한다.
    /// 부르는 쪽은 그 def를 통째로 거부해야 한다 - 절반만 반영된 물건은 없느니만 못하다.
    /// </summary>
    public static bool HasUnknown(
        string raw,
        string source,
        IEnumerable<string> headerKeys,
        params Type[] targets)
    {
        var known = new HashSet<string>(headerKeys);

        foreach (Type type in targets)
        {
            if (type != null)
                CollectSerialisedFields(type, known);
        }

        var unknown = new List<string>();

        foreach (string key in TopLevel(raw))
        {
            if (!known.Contains(key))
                unknown.Add(key);
        }

        if (unknown.Count == 0)
            return false;

        Debug.LogError(
            $"[Def] {source}: 아무도 모르는 키 {string.Join(", ", unknown)}. " +
            "오타이거나 컴포넌트를 빠뜨린 것이다 - 그냥 두면 조용히 기본값으로 묻힌다.");

        return true;
    }

    /// <summary>
    /// 타입이 실제로 직렬화하는 필드 이름. public 필드와 [SerializeField]가 붙은 private
    /// 필드, 그리고 [FormerlySerializedAs]의 옛 이름까지 - 옛 이름을 빼면 이름을 바꾼 날
    /// 아직 안 옮긴 def가 전부 깨진다.
    ///
    /// MonoBehaviour 위쪽은 안 본다. 거기 것들은 def가 건드릴 물건이 아니다.
    /// </summary>
    public static void CollectSerialisedFields(Type type, HashSet<string> into)
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
    public static List<string> TopLevel(string json)
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

    /// <summary>
    /// 최상위 key의 값만 갈아끼운 원문을 돌려준다. 나머지 키는 글자 하나까지 그대로 남는다.
    ///
    /// export가 필요로 한다. 함선 def에는 손으로 튜닝한 수치(drag, angleAccel...)가 들어 있고,
    /// export는 배치만 다시 뽑는다 - 통째로 직렬화해서 덮어쓰면 그 수치가 조용히 사라진다.
    /// key가 없으면 null.
    /// </summary>
    public static string ReplaceTopLevelValue(string json, string key, string newValue)
    {
        int depth = 0, valueStart = -1;
        bool inString = false, escaped = false, atKey = false;
        int stringStart = -1;
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

                    if (depth == 1)
                        pending = json.Substring(stringStart, i - stringStart);
                }

                continue;
            }

            if (atKey && depth == 1 && valueStart < 0 && !char.IsWhiteSpace(c))
                valueStart = i;

            switch (c)
            {
                case '"':
                    inString = true;
                    stringStart = i + 1;
                    break;

                case ':':
                    if (depth == 1 && pending == key)
                        atKey = true;

                    pending = null;
                    break;

                case '{':
                case '[':
                    depth++;
                    if (!atKey) pending = null;
                    break;

                case '}':
                case ']':
                    depth--;

                    // 값이 이 자리에서 닫혔다. 배열/객체면 depth가 1로 돌아온 지점,
                    // 스칼라면 아래 ',' 분기가 먼저 잡는다.
                    if (atKey && depth == 1 && valueStart >= 0)
                        return json.Substring(0, valueStart) + newValue + json.Substring(i + 1);

                    if (!atKey) pending = null;
                    break;

                case ',':
                    if (atKey && depth == 1 && valueStart >= 0)
                        return json.Substring(0, valueStart) + newValue + json.Substring(i);
                    break;
            }
        }

        return null;
    }

    /// <summary>
    /// 있으면 갈아끼우고, 없으면 끼워 넣는다.
    ///
    /// <see cref="ReplaceTopLevelValue"/>는 **없는 키를 못 만든다** - 못 찾으면 null이다.
    /// 그래서 설계도에 없던 키(런 중에만 생기는 상태)를 저장 파일에 얹으려면 이쪽이 필요하다.
    /// 없이 하려면 설계도 아홉 개에 빈 값을 미리 적어둬야 하는데, 그러면 새 배를 만들 때마다
    /// 사람이 그 한 줄을 기억해야 하고 잊으면 저장이 조용히 죽는다.
    ///
    /// **여는 중괄호 바로 뒤에 넣는다.** 끝에 넣으면 앞 항목에 쉼표를 붙여야 하는데 그 앞이
    /// 공백인지 값인지 세어야 한다. 앞에 넣으면 뒤에 쉼표 하나만 찍으면 되고, 뒤에 최소
    /// 한 항목이 있다는 것은 이미 아는 사실이다(키가 하나도 없는 def는 파싱을 못 통과한다).
    /// </summary>
    public static string UpsertTopLevelValue(string json, string key, string newValue)
    {
        string replaced = ReplaceTopLevelValue(json, key, newValue);

        if (replaced != null)
            return replaced;

        if (string.IsNullOrEmpty(json))
            return null;

        int brace = json.IndexOf('{');

        if (brace < 0)
            return null;

        // 뒤에 항목이 하나도 없으면 쉼표를 찍으면 안 된다. 여는 괄호 다음의 공백을 건너뛰고
        // 닫는 괄호가 바로 나오는지 본다.
        int after = brace + 1;

        while (after < json.Length && char.IsWhiteSpace(json[after]))
            after++;

        bool empty = after >= json.Length || json[after] == '}';

        return json.Substring(0, brace + 1)
             + $"\n  \"{key}\": {newValue}{(empty ? "" : ",")}"
             + json.Substring(brace + 1);
    }

    private static readonly Dictionary<string, Type> _typeCache = new();

    /// <summary>
    /// 클래스 이름 -> Type. 세 단계로 찾는다: `Type.GetType` → 어셈블리별 정규화 이름 →
    /// **짧은 이름**.
    ///
    /// 세 번째가 이 함수의 이유다. `asm.GetType`은 네임스페이스까지 정확히 맞아야 하므로
    /// URP의 Light2D를 붙이려면 def에 `UnityEngine.Rendering.Universal.Light2D`를 통째로
    /// 적어야 했다. 남의 어셈블리에 있는 컴포넌트를 데이터로 붙일 수 있다는 것이 이 설계의
    /// 값어치인데, 그 값을 쓰려면 매번 네임스페이스를 알아내야 하는 것은 앞뒤가 안 맞는다.
    /// 이제 `"Light2D"` 한 마디면 된다.
    ///
    /// **Component만 본다.** 짧은 이름은 당연히 겹치기 쉬운데, 여기 오는 이름은 전부
    /// thingClass 아니면 comps라 Component가 아닌 후보는 애초에 답이 될 수 없다. 이 한 줄이
    /// 후보를 수천에서 수십으로 줄인다.
    ///
    /// 그래도 둘 이상이면 **아무거나 고르지 않고 거부한다.** 조용히 하나를 고르면 어느 쪽이
    /// 걸릴지가 어셈블리 로드 순서에 달리고, 그건 기계마다 다를 수 있다. 정규화 이름을
    /// 적으라고 말하고 물러난다.
    /// </summary>
    public static Type Resolve(string name)
    {
        if (string.IsNullOrEmpty(name))
            return null;

        if (_typeCache.TryGetValue(name, out Type cached))
            return cached;

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

        found ??= ResolveShortName(name);

        _typeCache[name] = found;
        return found;
    }

    /// <summary>
    /// 네임스페이스 없이 클래스 이름만으로 찾는다. 느리지만 이름 하나당 딱 한 번이고,
    /// 결과는 <see cref="Resolve"/>가 캐시한다.
    /// </summary>
    private static Type ResolveShortName(string name)
    {
        // 이름이 이미 점을 포함하면 정규화 이름을 적은 것이고, 위에서 못 찾았다는 것은
        // 진짜로 없다는 뜻이다. 여기서 뒷부분만 떼어 비슷한 것을 집어오면 오타가 다른
        // 클래스로 조용히 성공한다 - def 검증이 막으려는 바로 그 실패다.
        if (name.IndexOf('.') >= 0)
            return null;

        List<Type> matches = null;

        foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;

            // 어셈블리 하나가 의존성을 못 찾아 던져도 나머지는 멀쩡하다. 이 경우 e.Types에
            // 성공한 것만 들어 있고 실패한 자리는 null이다.
            try
            {
                types = asm.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                types = e.Types;
            }
            catch
            {
                continue;
            }

            foreach (Type type in types)
            {
                if (type == null || type.Name != name)
                    continue;

                if (!typeof(Component).IsAssignableFrom(type) || type.IsAbstract)
                    continue;

                (matches ??= new List<Type>()).Add(type);
            }
        }

        if (matches == null)
            return null;

        if (matches.Count == 1)
            return matches[0];

        // 정렬해서 말한다. 순서가 뒤죽박죽이면 같은 에러가 실행마다 다르게 보인다.
        matches.Sort((a, b) => string.CompareOrdinal(a.FullName, b.FullName));

        var names = new List<string>(matches.Count);

        foreach (Type type in matches)
            names.Add(type.FullName);

        Debug.LogError(
            $"[Def] '{name}'이라는 클래스가 {matches.Count}개다: {string.Join(", ", names)}. " +
            "어느 것인지 정할 수 없으니 def에 전체 이름을 적어라.");

        return null;
    }
}
