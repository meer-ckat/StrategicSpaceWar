#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using Supertonic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Supertonic 이식이 살아있는지 확인한다. 원본은 System.Text.Json을 쓰는데 Unity에는
/// 그게 없어서 MiniJson(SupertonicCore.cs)으로 갈아끼웠다 - 갈아끼운 자리(unicode_indexer /
/// tts.json / voice_style의 중첩 float 배열)가 조용히 어긋나면 컴파일은 되고 소리만 안 나온다.
/// 그래서 파형까지 실제로 뽑아보고 값을 본다.
///
/// 참고: 모델이 노이즈를 샘플링하므로 같은 문장도 매번 파형이 다르다. 값을 고정해 비교하는
/// 검사는 여기 넣지 마라 - 반드시 깨진다.
///
/// 모델 로딩이 있어서 첫 실행은 5초쯤 걸린다.
/// </summary>
public static class SupertonicSelfTest
{
    private static int _pass;
    private static int _fail;

    [MenuItem("Tools/Supertonic/Run Supertonic Tests")]
    public static void Run()
    {
        _pass = 0;
        _fail = 0;

        // ---------------------------------------------------------
        // 1) 리치 텍스트 제거 - 대사에 <color=...>가 섞여 있다
        // ---------------------------------------------------------

        Check(SupertonicTts.StripRichText("접촉 전부 소실. <color=#8fd3ff>교전 종료.</color>")
              == "접촉 전부 소실. 교전 종료.",
            "리치 텍스트 태그를 벗긴다");

        Check(SupertonicTts.StripRichText(null) == "", "null은 빈 문자열");
        Check(SupertonicTts.StripRichText("") == "", "빈 문자열은 빈 문자열");

        // 표현 태그는 모델이 알아듣는 것이므로 벗기면 안 된다. 반대로 허용 목록에 없는
        // 태그는 벗겨야 한다 - 안 그러면 모델이 그 글자를 그대로 읽는다.
        Check(SupertonicTts.StripRichText("살아있네. <laugh> 다행이군.")
              == "살아있네. <laugh> 다행이군.",
            "표현 태그는 통과시킨다");

        Check(!SupertonicTts.StripRichText("<b>굵게</b> <smirk> 끝").Contains("<"),
            "허용 목록에 없는 태그는 벗긴다");

        if (!SupertonicTts.ModelsInstalled)
        {
            Debug.LogWarning($"모델이 없어 합성 검사를 건너뛴다: {SupertonicTts.ModelDir}\n" +
                             "tools/fetch-supertonic.ps1을 먼저 돌려라.");
            Report();
            return;
        }

        // ---------------------------------------------------------
        // 2) 실제 합성 - MiniJson 이식이 런타임에 살아있는가
        // ---------------------------------------------------------

        var t0 = DateTime.Now;
        var wav = SupertonicTts.Synthesize("접촉 전부 소실. 교전 종료.", "ko", "M1");
        double sec = (DateTime.Now - t0).TotalSeconds;
        int sr = SupertonicTts.SampleRate;

        Check(sr == 44100, $"sample rate == 44100 (실제 {sr})");
        Check(wav.Length > sr * 0.5,
            $"0.5초 넘는 오디오가 나온다 ({wav.Length / (float)sr:F2}초, 합성에 {sec:F1}초)");

        bool nan = false;
        float max = 0f;
        foreach (var s in wav)
        {
            if (float.IsNaN(s) || float.IsInfinity(s)) nan = true;
            max = Mathf.Max(max, Mathf.Abs(s));
        }

        Check(!nan, "NaN/Inf 없음");
        Check(max > 0.01f && max <= 1.5f, $"진폭이 정상 범위 (max {max:F3})");

        // ---------------------------------------------------------
        // 3) 보이스 파일이 실제로 갈린다
        //
        // 파형끼리 비교하지 않는 이유: 모델이 노이즈를 샘플링해서 같은 문장도 매번 다르다.
        // 그러면 M1과 M2를 바꿔치기해도 "다르다"가 통과해 버린다. 그래서 파싱 결과인
        // 스타일 벡터를 본다 - 이건 파일에서 온 값이라 결정적이다.
        // ---------------------------------------------------------

        var m1 = Helper.LoadVoiceStyle(new List<string> { Path.Combine(SupertonicTts.VoiceDir, "M1.json") });
        var m2 = Helper.LoadVoiceStyle(new List<string> { Path.Combine(SupertonicTts.VoiceDir, "M2.json") });

        Check(m1.Ttl.Length > 0 && m1.Ttl.Length == m2.Ttl.Length,
            $"스타일 벡터 길이가 같다 ({m1.Ttl.Length})");

        bool allZero = true;
        bool differs = false;
        for (int i = 0; i < m1.Ttl.Length; i++)
        {
            if (m1.Ttl[i] != 0f) allZero = false;
            if (Mathf.Abs(m1.Ttl[i] - m2.Ttl[i]) > 1e-6f) differs = true;
        }

        Check(!allZero, "스타일 벡터가 0으로 안 채워졌다 (파싱이 죽으면 여기가 걸린다)");
        Check(differs, "M1과 M2의 스타일 벡터가 다르다");

        // ---------------------------------------------------------
        // 4) wav 왕복 - 헤더 44바이트 + 16bit 샘플
        // ---------------------------------------------------------

        string p = Path.Combine(Application.temporaryCachePath, "supertonic_selftest.wav");
        SupertonicTts.WriteWav(p, wav, sr);
        var fi = new FileInfo(p);

        Check(fi.Exists && fi.Length == 44 + wav.Length * 2L,
            $"wav 크기 == 44 + 샘플수*2 ({fi.Length} bytes)");

        File.Delete(p);

        // ---------------------------------------------------------
        // 5) 클립 변환
        // ---------------------------------------------------------

        var clip = SupertonicTts.ToClip(wav, sr, "selftest");
        Check(clip != null && clip.samples == wav.Length && clip.channels == 1,
            "AudioClip으로 그대로 넘어간다");
        Check(SupertonicTts.ToClip(Array.Empty<float>(), sr, "empty") == null,
            "빈 오디오는 클립을 안 만든다");

        Report();
    }

    private static void Check(bool ok, string what)
    {
        if (ok) { _pass++; Debug.Log($"PASS  {what}"); }
        else { _fail++; Debug.LogError($"FAIL  {what}"); }
    }

    private static void Report()
        => Debug.Log(_fail == 0
            ? $"Supertonic 자가진단: {_pass}개 전부 통과"
            : $"Supertonic 자가진단: {_fail}개 실패 / {_pass + _fail}개");
}
#endif
