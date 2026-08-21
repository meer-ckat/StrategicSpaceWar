#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Dialogue Visual v2 validator.
/// Runtime DialogueScript/DialogueLine을 compile-time으로 참조하지 않는다.
/// 대신 reflection으로 실제 runtime field/style/lane을 읽어서 validator와 engine의 버전 스큐를 막는다.
/// </summary>
public static class StoryScriptValidator
{
    [Serializable]
    private sealed class ScriptDto
    {
        public string defName;
        public bool pickOne;
        public float cooldown;
        public LineDto[] lines;
    }

    [Serializable]
    private sealed class LineDto
    {
        public string message;
        public string author;
        public float duration = 4f;
        public float intensity = 1f;
        public string style = "radio";
        public float signalQuality = 1f;
        public bool interrupt;
        public float wait;
    }

    private sealed class Report
    {
        public int files;
        public int errors;
        public int warnings;
        public readonly Dictionary<string, string> defOwners = new(StringComparer.Ordinal);

        public void Error(string file, string message)
        {
            errors++;
            Debug.LogError($"[Dialogue Validator] {Short(file)}: {message}");
        }

        public void Warn(string file, string message)
        {
            warnings++;
            Debug.LogWarning($"[Dialogue Validator] {Short(file)}: {message}");
        }

        private static string Short(string path)
        {
            string root = Application.dataPath.Replace('\\', '/');
            string p = path.Replace('\\', '/');
            return p.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? "Assets" + p.Substring(root.Length)
                : p;
        }
    }

    private static Type _managerType;
    private static Type _scriptType;
    private static Type _lineType;
    private static MethodInfo _knownStyle;
    private static MethodInfo _laneForStyle;

    [MenuItem("Tools/Dialogue/Validate All Scripts")]
    public static void ValidateAll()
    {
        string folder = Path.Combine(Application.streamingAssetsPath, "대사");
        if (!Directory.Exists(folder))
        {
            Debug.LogWarning($"[Dialogue Validator] 대사 폴더가 없다: {folder}");
            return;
        }

        string[] files = Directory.GetFiles(folder, "*.json", SearchOption.AllDirectories);
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        var report = new Report();

        foreach (string file in files)
        {
            report.files++;
            ValidateFile(file, report);
        }

        string summary = $"[Dialogue Validator] {report.files} files / {report.errors} errors / {report.warnings} warnings";
        if (report.errors > 0) Debug.LogError(summary);
        else if (report.warnings > 0) Debug.LogWarning(summary);
        else Debug.Log(summary + " — clean.");
    }

    [MenuItem("Assets/Dialogue/Validate Selected JSON", true)]
    private static bool CanValidateSelected()
    {
        string path = AssetDatabase.GetAssetPath(Selection.activeObject);
        return !string.IsNullOrEmpty(path) && path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    }

    [MenuItem("Assets/Dialogue/Validate Selected JSON")]
    private static void ValidateSelected()
    {
        string assetPath = AssetDatabase.GetAssetPath(Selection.activeObject);
        string absolute = Path.GetFullPath(assetPath);
        var report = new Report { files = 1 };
        ValidateFile(absolute, report);

        if (report.errors == 0 && report.warnings == 0)
            Debug.Log($"[Dialogue Validator] {assetPath} — clean.");
        else
            Debug.Log($"[Dialogue Validator] {assetPath} — {report.errors} errors / {report.warnings} warnings");
    }

    private static void ValidateFile(string file, Report report)
    {
        string json;
        try { json = File.ReadAllText(file); }
        catch (Exception e)
        {
            report.Error(file, "파일을 읽지 못했다: " + e.Message);
            return;
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            report.Error(file, "빈 JSON 파일이다.");
            return;
        }

        ValidateUnknownKeys(file, json, report);

        ScriptDto script;
        try { script = JsonUtility.FromJson<ScriptDto>(json); }
        catch (Exception e)
        {
            report.Error(file, "JSON 파싱 실패: " + e.Message);
            return;
        }

        if (script == null)
        {
            report.Error(file, "JSON을 DialogueScript 형태로 읽지 못했다.");
            return;
        }

        string fileName = Path.GetFileNameWithoutExtension(file);
        if (string.IsNullOrWhiteSpace(script.defName))
        {
            report.Error(file, "defName이 비어 있다.");
        }
        else
        {
            if (!string.Equals(script.defName, fileName, StringComparison.Ordinal))
                report.Warn(file, $"defName '{script.defName}' != 파일명 '{fileName}'. Play()은 파일명으로 찾는다.");

            if (report.defOwners.TryGetValue(script.defName, out string owner))
                report.Error(file, $"defName '{script.defName}' 중복. 먼저 나온 파일: {owner}");
            else
                report.defOwners.Add(script.defName, file);
        }

        if (script.cooldown < 0f)
            report.Error(file, $"cooldown은 0 이상이어야 한다. 현재 {script.cooldown}.");

        if (script.lines == null || script.lines.Length == 0)
        {
            report.Error(file, "lines가 없거나 비어 있다.");
            return;
        }

        for (int i = 0; i < script.lines.Length; i++)
            ValidateLine(file, script, script.lines[i], i, report);

        ValidateBurst(file, script, report);
    }

    private static void ValidateLine(string file, ScriptDto script, LineDto line, int index, Report report)
    {
        string at = $"lines[{index}]";
        if (line == null)
        {
            report.Error(file, at + "가 null이다.");
            return;
        }

        if (string.IsNullOrWhiteSpace(line.message))
            report.Error(file, at + ".message가 비어 있다.");
        else
            ValidateRichText(file, at, line.message, report);

        if (line.duration < 0f) report.Error(file, $"{at}.duration은 0 이상이어야 한다. 현재 {line.duration}.");
        if (line.intensity < 0f) report.Error(file, $"{at}.intensity는 0 이상이어야 한다. 현재 {line.intensity}.");
        if (line.wait < 0f) report.Error(file, $"{at}.wait는 0 이상이어야 한다. 현재 {line.wait}.");
        if (line.signalQuality < 0f || line.signalQuality > 1f)
            report.Error(file, $"{at}.signalQuality는 0~1이어야 한다. 현재 {line.signalQuality}.");

        string style = string.IsNullOrWhiteSpace(line.style) ? "radio" : line.style.Trim();
        if (!RuntimeKnowsStyle(style))
            report.Error(file, $"{at}.style '{line.style}'은 현재 StoryScriptManager가 모르는 style이다.");

        if (script.pickOne && line.wait > 0f)
            report.Warn(file, $"{at}.wait={line.wait}지만 pickOne=true에서는 사용되지 않는다.");

        if (line.interrupt && line.intensity <= 0f)
            report.Warn(file, $"{at}는 interrupt=true인데 intensity={line.intensity}. 난입 연출이 죽는다.");

        if (line.interrupt && line.signalQuality < 0.5f)
            report.Warn(file, $"{at}는 critical interrupt인데 signalQuality={line.signalQuality:0.00}. 중요한 정보는 body가 읽혀야 한다.");

        string lane = RuntimeLane(style);
        if (lane == "system" && !string.IsNullOrWhiteSpace(line.author))
            report.Warn(file, $"{at}는 system style인데 author='{line.author}'. v2 system strip은 화자를 표시하지 않는다.");

        int rows = EstimateRows(line.message, lane);
        if (rows > 2)
            report.Warn(file, $"{at}.message는 {lane} lane에서 약 {rows}줄로 보인다. 전투 자막은 2줄 이하 권장.");

        float reading = EstimateReadingSeconds(line.message);
        if (line.duration > 0f && line.duration + 0.1f < reading)
            report.Warn(file, $"{at}.duration={line.duration:0.0}s, 예상 읽기 시간≈{reading:0.0}s. 너무 빨리 사라질 수 있다.");
    }

    /// <summary>JsonUtility가 조용히 버리는 오타를 잡는다. runtime public field를 reflection으로 읽어 union을 만든다.</summary>
    private static void ValidateUnknownKeys(string file, string json, Report report)
    {
        HashSet<string> allowed = RuntimeFieldNames();
        if (allowed.Count == 0)
        {
            report.Warn(file, "runtime DialogueScript/DialogueLine reflection 실패 — unknown key 검사를 건너뛴다.");
            return;
        }

        foreach (string key in ScanJsonKeys(json))
        {
            if (!allowed.Contains(key))
                report.Error(file, $"unknown JSON key '{key}'. JsonUtility는 이 오타를 조용히 무시한다.");
        }
    }

    private static HashSet<string> RuntimeFieldNames()
    {
        EnsureRuntimeReflection();
        var set = new HashSet<string>(StringComparer.Ordinal);
        AddPublicFields(_scriptType, set);
        AddPublicFields(_lineType, set);
        return set;
    }

    private static void AddPublicFields(Type type, HashSet<string> set)
    {
        if (type == null) return;
        foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public))
            set.Add(field.Name);
    }

    private static IEnumerable<string> ScanJsonKeys(string json)
    {
        for (int i = 0; i < json.Length; i++)
        {
            if (json[i] != '"') continue;
            int start = ++i;
            var sb = new StringBuilder();
            bool escaped = false;

            for (; i < json.Length; i++)
            {
                char c = json[i];
                if (escaped) { sb.Append(c); escaped = false; continue; }
                if (c == '\\') { escaped = true; continue; }
                if (c == '"') break;
                sb.Append(c);
            }

            int j = i + 1;
            while (j < json.Length && char.IsWhiteSpace(json[j])) j++;
            if (j < json.Length && json[j] == ':')
                yield return sb.ToString();
        }
    }

    private static void EnsureRuntimeReflection()
    {
        if (_managerType != null) return;

        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            _managerType ??= assembly.GetType("StoryScriptManager", false);
            _scriptType ??= assembly.GetType("DialogueScript", false);
            _lineType ??= assembly.GetType("DialogueLine", false);
        }

        _knownStyle = _managerType?.GetMethod("IsKnownStyle", BindingFlags.Public | BindingFlags.Static);
        _laneForStyle = _managerType?.GetMethod("PresentationLaneForStyle", BindingFlags.Public | BindingFlags.Static);
    }

    private static bool RuntimeKnowsStyle(string style)
    {
        EnsureRuntimeReflection();
        if (_knownStyle == null) return true; // manager가 아직 compile 안 된 동안 validator가 연쇄 에러를 만들지 않는다.
        return _knownStyle.Invoke(null, new object[] { style }) is bool known && known;
    }

    private static string RuntimeLane(string style)
    {
        EnsureRuntimeReflection();
        if (_laneForStyle == null) return "external";
        return _laneForStyle.Invoke(null, new object[] { style }) as string ?? "external";
    }

    private static int EstimateRows(string message, string lane)
    {
        float capacity = lane switch
        {
            "internal" => 30f,
            "system" => 38f,
            _ => 42f,
        };

        float units = 0f;
        bool tag = false;
        foreach (char c in message ?? string.Empty)
        {
            if (c == '<') { tag = true; continue; }
            if (c == '>') { tag = false; continue; }
            if (tag) continue;
            if (c == '\n') { units = Mathf.Ceil(units / capacity) * capacity; continue; }
            if (char.IsWhiteSpace(c)) units += 0.35f;
            else units += c <= 0x7F ? 0.55f : 1f;
        }

        return Mathf.Max(1, Mathf.CeilToInt(units / capacity));
    }

    private static float EstimateReadingSeconds(string message)
    {
        int visible = 0;
        bool tag = false;
        foreach (char c in message ?? string.Empty)
        {
            if (c == '<') { tag = true; continue; }
            if (c == '>') { tag = false; continue; }
            if (!tag && !char.IsWhiteSpace(c)) visible++;
        }

        return Mathf.Max(1.4f, 0.7f + visible / 12f);
    }

    /// <summary>같은 lane에 카드가 과도하게 겹치는 대본을 대략적으로 잡는다.</summary>
    private static void ValidateBurst(string file, ScriptDto script, Report report)
    {
        if (script.pickOne || script.lines == null) return;

        var activeUntil = new Dictionary<string, List<float>>(StringComparer.Ordinal);
        float time = 0f;

        for (int i = 0; i < script.lines.Length; i++)
        {
            LineDto line = script.lines[i];
            if (line == null || string.IsNullOrWhiteSpace(line.message)) continue;

            string lane = RuntimeLane(line.style);
            if (!activeUntil.TryGetValue(lane, out List<float> ends))
            {
                ends = new List<float>();
                activeUntil.Add(lane, ends);
            }

            ends.RemoveAll(end => end <= time);
            float reading = EstimateReadingSeconds(line.message);
            float lifetime = Mathf.Max(line.duration, reading);
            ends.Add(time + lifetime);

            int recommended = lane == "system" ? 1 : 3;
            if (ends.Count > recommended)
                report.Warn(file, $"lines[{i}] 시점에 {lane} lane 동시 표시 예상 {ends.Count}개. v2 권장 {recommended}개 이하.");

            float next = line.wait > 0f ? line.wait : Mathf.Max(0.6f, reading * 0.60f);
            time += next;
        }
    }

    private static void ValidateRichText(string file, string at, string text, Report report)
    {
        var stack = new Stack<string>();
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '<') continue;
            int end = text.IndexOf('>', i + 1);
            if (end < 0)
            {
                report.Error(file, at + ".message에 닫히지 않은 '<' 태그가 있다.");
                return;
            }

            string raw = text.Substring(i + 1, end - i - 1).Trim();
            i = end;
            if (raw.Length == 0 || raw.StartsWith("!", StringComparison.Ordinal)) continue;

            bool closing = raw[0] == '/';
            bool selfClosing = raw.EndsWith("/", StringComparison.Ordinal);
            string body = closing ? raw.Substring(1).TrimStart() : raw;
            int cut = body.IndexOfAny(new[] { ' ', '=', '/' });
            string name = (cut >= 0 ? body.Substring(0, cut) : body).Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(name) || name == "br" || selfClosing) continue;

            if (!closing) { stack.Push(name); continue; }
            if (stack.Count == 0)
            {
                report.Error(file, $"{at}.message에 여는 태그 없는 </{name}>가 있다.");
                continue;
            }

            string expected = stack.Pop();
            if (!string.Equals(expected, name, StringComparison.Ordinal))
                report.Error(file, $"{at}.message rich-text nesting 오류: </{name}>가 왔지만 </{expected}>를 닫아야 한다.");
        }

        while (stack.Count > 0)
            report.Error(file, $"{at}.message에 닫히지 않은 <{stack.Pop()}> 태그가 있다.");
    }
}
#endif