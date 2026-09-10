#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using Supertonic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// A안: 대본을 에디터에서 미리 wav로 굽는다. 빌드에는 wav만 들어가므로 ONNX 런타임도
/// 383 MB 모델도 출시 빌드에 안 실린다 - 모델을 배포하지 않으니 OpenRAIL 4조(라이선스
/// 동봉·사용제한 고지) 의무도 안 붙는다. 크레딧에 "AI 합성 음성" 한 줄만 남기면 된다.
///
/// 대본은 ScriptManager가 읽는다. 여기서 JSON을 따로 파싱하지 않는 이유는 형식이 둘로
/// 갈리는 걸 막기 위해서다 - 대본에 필드가 하나 늘 때 고칠 자리가 하나여야 한다.
///
/// 이미 있는 파일은 건너뛴다. 대본 한 줄만 고치고 다시 돌려도 그 줄만 새로 굽는다 -
/// 한 줄에 1초 안팎이 걸리므로 전부 다시 굽는 건 그냥 낭비다. 다 다시 굽고 싶으면
/// TtsBaked 폴더를 지워라.
/// </summary>
public static class SupertonicBaker
{
    private const string OutFolder = "TtsBaked";

    /// <summary>
    /// 화자별 보이스. 한 목소리로 전부 읽으면 누가 말하는지 안 들려서 대본을 나눈 의미가 없다.
    /// 표에 없는 화자는 <see cref="SupertonicTts.DefaultVoice"/>.
    /// </summary>
    private static readonly Dictionary<string, string> VoiceByAuthor = new Dictionary<string, string>
    {
        { "함장", "M1" },
        { "전술", "M2" },
        { "기관", "M3" },
        { "통신", "F1" },
        { "관제", "F2" },
    };

    [MenuItem("Tools/Supertonic/대사 전부 굽기")]
    public static void BakeAll()
    {
        if (!SupertonicTts.ModelsInstalled)
        {
            Debug.LogError($"Supertonic 모델이 없다: {SupertonicTts.ModelDir} - tools/fetch-supertonic.ps1을 먼저 돌려라.");
            return;
        }

        string outDir = Path.Combine(Application.streamingAssetsPath, OutFolder);
        Directory.CreateDirectory(outDir);

        // 캐시된 옛 대본을 굽지 않도록. 에디터에서 방금 고쳤을 수 있다.
        ScriptManager.ReloadScripts();

        var files = Directory.GetFiles(ScriptManager.ScriptFolder, "*.json");
        int baked = 0, skipped = 0;

        try
        {
            for (int f = 0; f < files.Length; f++)
            {
                string defName = Path.GetFileNameWithoutExtension(files[f]);
                DialogueScript script = ScriptManager.LoadScript(defName);
                if (script?.lines == null) continue;

                string bookDir = Path.Combine(outDir, defName);
                Directory.CreateDirectory(bookDir);

                for (int i = 0; i < script.lines.Length; i++)
                {
                    DialogueLine line = script.lines[i];
                    string text = SupertonicTts.StripRichText(line.message);
                    if (string.IsNullOrEmpty(text)) continue;

                    string wavPath = Path.Combine(bookDir, $"{i:00}.wav");
                    if (File.Exists(wavPath)) { skipped++; continue; }

                    string voice = VoiceByAuthor.TryGetValue(line.author ?? "", out var v)
                        ? v
                        : SupertonicTts.DefaultVoice;

                    if (EditorUtility.DisplayCancelableProgressBar(
                            "Supertonic 굽는 중",
                            $"{defName} [{i + 1}/{script.lines.Length}] {line.author}: {text}",
                            (float)f / files.Length))
                    {
                        Debug.LogWarning($"취소됨. {baked}줄 구웠다.");
                        return;
                    }

                    var wav = SupertonicTts.Synthesize(text, "ko", voice);
                    SupertonicTts.WriteWav(wavPath, wav, SupertonicTts.SampleRate);
                    baked++;
                }
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            AssetDatabase.Refresh();
        }

        Debug.Log($"Supertonic: {baked}줄 새로 구웠다 (이미 있어서 건너뛴 것 {skipped}줄). → StreamingAssets/{OutFolder}");
    }

    [MenuItem("Tools/Supertonic/모델 내리기 (383MB 회수)")]
    public static void Unload()
    {
        SupertonicTts.Unload();
        System.GC.Collect();
        Debug.Log("Supertonic 세션 내림.");
    }

    [MenuItem("Tools/Supertonic/구운 wav 전부 지우기")]
    public static void Clear()
    {
        string outDir = Path.Combine(Application.streamingAssetsPath, OutFolder);
        if (!Directory.Exists(outDir)) return;

        int n = Directory.GetFiles(outDir, "*.wav", SearchOption.AllDirectories).Length;
        if (!EditorUtility.DisplayDialog("구운 wav 지우기", $"{n}개를 지운다. 되돌릴 수 없다.", "지운다", "관둔다"))
            return;

        Directory.Delete(outDir, true);
        File.Delete(outDir + ".meta");
        AssetDatabase.Refresh();
        Debug.Log($"wav {n}개 지움.");
    }
}
#endif
