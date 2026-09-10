using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEngine;

namespace Supertonic
{
    /// <summary>
    /// Supertonic 3(ONNX 온디바이스 TTS) 진입점. 대본 굽기(에디터)와 즉석 합성(런타임)이
    /// 같은 세션을 쓴다.
    ///
    /// 모델은 처음 부를 때 한 번만 올라간다 - 4개 합쳐 383 MB고 로딩이 수 초 걸린다.
    /// 한 번 올린 세션은 <see cref="Unload"/>를 부르기 전까지 안 내려간다.
    ///
    /// 모델 파일은 저장소에 없다(.gitignore). 새로 클론했으면 tools/fetch-supertonic.ps1을
    /// 먼저 돌려라. 없으면 <see cref="ModelsInstalled"/>가 false다.
    /// </summary>
    public static class SupertonicTts
    {
        /// <summary>기본 보이스. voice_styles 폴더의 파일 이름(M1~M5, F1~F5)이 곧 이름이다.</summary>
        public const string DefaultVoice = "M1";

        /// <summary>디노이즈 단계. 8이 기본, 올리면 품질이 오르고 그만큼 느려진다.</summary>
        public const int DefaultSteps = 8;

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Style> StyleCache = new Dictionary<string, Style>();

        // <...> 꼴은 두 종류가 섞여 들어온다. 화면용 리치 텍스트(<color=#8fd3ff>, </color>, <b>)와
        // 모델이 알아듣는 표현 태그(<laugh> 등)다. 전자는 벗겨야 하고 - 안 벗기면 모델이
        // "색깔 등호 샵"을 그대로 읽는다 - 후자는 그대로 넘겨야 소리가 난다.
        //
        // 허용 목록 방식이다. 모르는 태그는 벗긴다. 반대로 했다가 오타 하나가 모델에
        // 흘러가면 그 문장을 통째로 읽어버리는데, 대사가 수백 줄이면 어느 줄인지 못 찾는다.
        private static readonly Regex AnyTag = new Regex("<([^>]*)>");

        /// <summary>
        /// 모델이 알아듣는 표현 태그. 이 목록에 있는 것만 <see cref="StripRichText"/>를 통과한다.
        ///
        /// 공식 문서는 태그가 10종이라고만 하고 목록을 안 밝혔다 (2026-08 기준). 확인된 셋만
        /// 넣어뒀다. 나머지는 Tools > Supertonic > 표현 태그 시험 굽기로 후보를 구워서
        /// 들어보고, 실제로 소리가 나는 것만 여기 추가해라.
        /// </summary>
        public static readonly HashSet<string> ExpressionTags = new HashSet<string>
        {
            "laugh", "breath", "sigh",
        };

        private static TextToSpeech _tts;

        public static string ModelDir => Path.Combine(Application.streamingAssetsPath, "Supertonic", "onnx");
        public static string VoiceDir => Path.Combine(Application.streamingAssetsPath, "Supertonic", "voice_styles");

        /// <summary>모델 파일이 실제로 깔려 있는가. 제일 큰 놈 하나만 확인한다.</summary>
        public static bool ModelsInstalled => File.Exists(Path.Combine(ModelDir, "vector_estimator.onnx"));

        public static int SampleRate => Session().SampleRate;

        /// <summary>
        /// 화면용 태그를 벗기고 표현 태그는 남긴 읽을 거리. 빈 문자열이면 읽을 게 없다는 뜻이다.
        /// </summary>
        public static string StripRichText(string message)
        {
            if (string.IsNullOrEmpty(message)) return "";

            return AnyTag.Replace(message, m =>
                ExpressionTags.Contains(m.Groups[1].Value) ? m.Value : "").Trim();
        }

        /// <summary>
        /// 합성해서 PCM을 돌려준다. 메인 스레드에서 부르면 그 시간만큼 프레임이 멈춘다 -
        /// 런타임에서는 <see cref="SynthesizeAsync"/>를 써라.
        /// </summary>
        public static float[] Synthesize(
            string text, string lang = "ko", string voice = DefaultVoice, int totalStep = DefaultSteps)
            => SynthesizeRaw(StripRichText(text), lang, voice, totalStep);

        /// <summary>
        /// 태그를 벗기지 않고 그대로 넣는다. 표현 태그 후보를 시험할 때만 쓴다 -
        /// 대사를 읽을 때 이걸 부르면 화면용 리치 텍스트까지 모델이 읽어버린다.
        /// </summary>
        public static float[] SynthesizeRaw(
            string text, string lang = "ko", string voice = DefaultVoice, int totalStep = DefaultSteps)
        {
            if (string.IsNullOrEmpty(text)) return Array.Empty<float>();

            // ponytail: 세션 전체를 락 하나로 막는다. ORT 자체는 동시 Run이 되지만 지금은
            // 부르는 자리가 굽기 루프 하나와 대사 한 줄뿐이라 경합이 없다. 여러 화자가
            // 동시에 떠들어야 하면 그때 세션을 화자별로 쪼개라.
            lock (Gate)
            {
                var (wav, _) = Session().Call(text, lang, Voice(voice), totalStep);
                return wav;
            }
        }

        /// <summary>백그라운드에서 합성한다. await한 자리는 메인 스레드로 돌아온다.</summary>
        public static Task<float[]> SynthesizeAsync(
            string text, string lang = "ko", string voice = DefaultVoice, int totalStep = DefaultSteps)
            => Task.Run(() => Synthesize(text, lang, voice, totalStep));

        /// <summary>합성해서 바로 재생 가능한 클립으로. 메인 스레드에서만 부를 것.</summary>
        public static async Task<AudioClip> SpeakAsync(
            string text, string lang = "ko", string voice = DefaultVoice, int totalStep = DefaultSteps)
        {
            var wav = await SynthesizeAsync(text, lang, voice, totalStep);
            return ToClip(wav, SampleRate, "tts");
        }

        /// <summary>PCM을 AudioClip으로. AudioClip 생성은 메인 스레드 전용이다.</summary>
        public static AudioClip ToClip(float[] wav, int sampleRate, string name)
        {
            if (wav == null || wav.Length == 0) return null;
            var clip = AudioClip.Create(name, wav.Length, 1, sampleRate, false);
            clip.SetData(wav, 0);
            return clip;
        }

        /// <summary>PCM을 16-bit wav 파일로.</summary>
        public static void WriteWav(string path, float[] wav, int sampleRate)
            => Helper.WriteWavFile(path, wav, sampleRate);

        /// <summary>세션을 내린다. 383 MB가 돌아온다.</summary>
        public static void Unload()
        {
            lock (Gate)
            {
                _tts = null;
                StyleCache.Clear();
            }
        }

        private static TextToSpeech Session()
        {
            if (_tts != null) return _tts;
            lock (Gate)
            {
                if (_tts == null)
                {
                    if (!ModelsInstalled)
                        throw new FileNotFoundException(
                            $"Supertonic 모델이 없다: {ModelDir} - tools/fetch-supertonic.ps1을 먼저 돌려라.");
                    _tts = Helper.LoadTextToSpeech(ModelDir);
                }
            }
            return _tts;
        }

        private static Style Voice(string voice)
        {
            if (StyleCache.TryGetValue(voice, out var cached)) return cached;

            var path = Path.Combine(VoiceDir, voice + ".json");
            if (!File.Exists(path))
                throw new FileNotFoundException($"보이스가 없다: {path}");

            var style = Helper.LoadVoiceStyle(new List<string> { path });
            StyleCache[voice] = style;
            return style;
        }
    }
}
