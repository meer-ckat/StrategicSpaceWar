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

    private static readonly Dictionary<string, Type> _typeCache = new();

    /// <summary>
    /// 클래스 이름 -> Type. 같은 어셈블리면 GetType 한 번으로 끝나고, 아니면 전부 훑는다 -
    /// URP의 Light2D처럼 남의 어셈블리에 있는 컴포넌트도 def가 부를 수 있어야 한다.
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

        _typeCache[name] = found;
        return found;
    }
}
