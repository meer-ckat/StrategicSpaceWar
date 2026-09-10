#if UNITY_EDITOR
using System.IO;
using Supertonic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 표현 태그 후보를 하나씩 구워서 귀로 확인하는 도구.
///
/// 왜 필요한가: 공식 문서는 표현 태그가 10종이라고만 하고 `&lt;laugh&gt;`, `&lt;breath&gt;`,
/// `&lt;sigh&gt;` 셋만 예로 든다 (2026-08 기준). 나머지 일곱을 알려주는 곳이 없다.
///
/// 길이로는 구분이 안 된다 - 모델이 모르는 태그를 글자 그대로 읽어도 출력이 딱 그만큼
/// 길어지기 때문에, 진짜 태그와 가짜 태그의 길이 차이가 같은 방향으로 난다. 결국 들어봐야
/// 안다: 진짜면 웃음/한숨 같은 비언어 소리가 나고, 가짜면 "래프"처럼 글자를 읽는다.
///
/// 확인한 태그를 <see cref="SupertonicTts.ExpressionTags"/>에 추가하면 그때부터
/// 대사에서 쓸 수 있다. 추가 전에는 StripRichText가 벗겨낸다.
/// </summary>
public static class SupertonicTagProbe
{
    /// <summary>확인용 문장. 태그 앞뒤에 말이 있어야 태그만 따로 들린다.</summary>
    private const string ProbeText = "살아있네. <TAG> 다행이군.";

    /// <summary>후보. 공식 목록이 없어서 흔한 비언어 표현으로 채웠다.</summary>
    private static readonly string[] Candidates =
    {
        "laugh", "breath", "sigh",                       // 문서에 나온 셋 - 기준점이다
        "cough", "gasp", "whisper", "yawn", "chuckle",
        "giggle", "cry", "sob", "scream", "groan",
        "sniff", "hmm", "inhale", "exhale", "clear_throat",
    };

    [MenuItem("Tools/Supertonic/표현 태그 시험 굽기")]
    public static void Probe()
    {
        if (!SupertonicTts.ModelsInstalled)
        {
            Debug.LogError($"Supertonic 모델이 없다: {SupertonicTts.ModelDir}");
            return;
        }

        string dir = Path.Combine(Application.streamingAssetsPath, "TtsBaked", "_tag_probe");
        Directory.CreateDirectory(dir);

        try
        {
            // 기준점. 태그 없이 같은 문장을 구워 둔다 - 비교 대상이 없으면 판단이 안 선다.
            Bake(Path.Combine(dir, "00_없음.wav"), ProbeText.Replace("<TAG>", "").Replace("  ", " "));

            for (int i = 0; i < Candidates.Length; i++)
            {
                if (EditorUtility.DisplayCancelableProgressBar(
                        "표현 태그 시험", $"<{Candidates[i]}>", (float)i / Candidates.Length))
                    return;

                // 태그를 여기서만은 벗기면 안 된다. 아직 허용 목록에 없는 후보를 시험하는
                // 중이므로 StripRichText를 거치지 않고 곧장 모델에 넣는다.
                Bake(Path.Combine(dir, $"{i + 1:00}_{Candidates[i]}.wav"),
                    ProbeText.Replace("<TAG>", $"<{Candidates[i]}>"));
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            AssetDatabase.Refresh();
        }

        Debug.Log($"표현 태그 후보 {Candidates.Length}개 구웠다. 들어보고 진짜인 것만 " +
                  $"SupertonicTts.ExpressionTags에 추가해라.\n{dir}");
        EditorUtility.RevealInFinder(dir);
    }

    private static void Bake(string path, string textWithTag)
    {
        var wav = SupertonicTts.SynthesizeRaw(textWithTag, "ko", SupertonicTts.DefaultVoice);
        SupertonicTts.WriteWav(path, wav, SupertonicTts.SampleRate);
    }
}
#endif
