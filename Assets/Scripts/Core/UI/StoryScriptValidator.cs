#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 대사 JSON을 플레이하기 전에 전부 읽어 보는 싸구려지만 효과 큰 CI 전초기지.
/// Assets/Editor 아래에 둔다.
/// </summary>
public static class StoryScriptValidator
{
    [MenuItem("Tools/SUPERRADIANCE/Validate Dialogue Scripts")]
    public static void ValidateAll()
    {
        string folder = StoryScriptManager.ScriptFolder;

        if (!Directory.Exists(folder))
        {
            Debug.LogWarning($"[Story] 대사 폴더가 없다: {folder}");
            return;
        }

        string[] files = Directory.GetFiles(folder, "*.json", SearchOption.AllDirectories);
        int errors = 0;
        int warnings = 0;
        int scripts = 0;
        int lines = 0;

        for (int i = 0; i < files.Length; i++)
        {
            string path = files[i];
            string fileName = Path.GetFileNameWithoutExtension(path);

            try
            {
                DialogueScript script =
                    JsonUtility.FromJson<DialogueScript>(File.ReadAllText(path));

                if (script == null)
                {
                    Debug.LogError($"[Story] JSON parse 결과가 null: {path}");
                    errors++;
                    continue;
                }

                scripts++;

                if (script.lines == null || script.lines.Length == 0)
                {
                    Debug.LogWarning($"[Story] lines 없음: {path}");
                    warnings++;
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(script.defName) &&
                    !string.Equals(fileName, script.defName, StringComparison.Ordinal))
                {
                    Debug.LogWarning(
                        $"[Story] 파일명/defName 불일치: {fileName} != {script.defName}");
                    warnings++;
                }

                string mode = string.IsNullOrWhiteSpace(script.queueMode)
                    ? "enqueue"
                    : script.queueMode.ToLowerInvariant();

                if (mode != "enqueue" &&
                    mode != "drop" &&
                    mode != "coalesce" &&
                    mode != "replace")
                {
                    Debug.LogError(
                        $"[Story] queueMode 오류: {fileName} -> {script.queueMode}");
                    errors++;
                }

                for (int lineIndex = 0; lineIndex < script.lines.Length; lineIndex++)
                {
                    DialogueLine line = script.lines[lineIndex];

                    if (line == null)
                    {
                        Debug.LogWarning(
                            $"[Story] null line: {fileName}[{lineIndex}]");
                        warnings++;
                        continue;
                    }

                    bool hasWork =
                        !string.IsNullOrWhiteSpace(line.message) ||
                        !string.IsNullOrWhiteSpace(line.messageKey) ||
                        !string.IsNullOrWhiteSpace(line.voice) ||
                        !string.IsNullOrWhiteSpace(line.signal);

                    if (!hasWork)
                    {
                        Debug.LogWarning(
                            $"[Story] 빈 line: {fileName}[{lineIndex}]");
                        warnings++;
                    }

                    if (line.duration < 0f || line.wait < 0f)
                    {
                        Debug.LogError(
                            $"[Story] 음수 timing: {fileName}[{lineIndex}]");
                        errors++;
                    }

                    lines++;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[Story] JSON parse 실패: {path}\n{e}");
                errors++;
            }
        }

        StoryScriptManager.ReloadScripts();

        string result =
            $"[Story] validation 완료: {scripts} scripts, {lines} lines, " +
            $"{warnings} warnings, {errors} errors";

        if (errors > 0)
            Debug.LogError(result);
        else if (warnings > 0)
            Debug.LogWarning(result);
        else
            Debug.Log(result);
    }
}
#endif