using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Supertonic
{
    // ============================================================================
    // 최소 JSON 읽기
    // ============================================================================

    /// <summary>
    /// 이 파일이 읽는 JSON은 셋뿐이다: 정수 배열 하나(unicode_indexer), 정수 네 개(tts.json),
    /// 그리고 중첩 float 배열(voice_style). 셋 다 "정해진 키 뒤 대괄호 구간의 숫자를 나온
    /// 순서대로" 읽으면 끝난다 - voice_style의 3중 배열도 어차피 바로 1차원으로 펴서 쓰므로
    /// 구조를 세울 필요가 없다.
    ///
    /// 그래서 JSON 라이브러리를 안 쓴다. Unity에는 System.Text.Json이 없고, Newtonsoft를
    /// 끌어오면 패키지가 하나 늘고 300KB짜리 스타일 파일을 객체 트리로 지었다가 도로
    /// 펴는 낭비가 붙는다.
    ///
    /// 범용 파서가 아니다. 문자열 값 안에 대괄호나 숫자가 들어 있으면 잘못 읽는다 -
    /// 여기서 읽는 세 파일에는 그런 게 없다. 다른 JSON에 쓰지 마라.
    /// </summary>
    internal static class MiniJson
    {
        /// <summary>path를 순서대로 따라 내려가 그 키의 숫자 값 하나를 읽는다.</summary>
        public static int Int(string s, params string[] path)
        {
            int at = Seek(s, path);
            while (at < s.Length && !IsNum(s[at])) at++;
            int end = at;
            while (end < s.Length && IsNum(s[end])) end++;
            return int.Parse(s.Substring(at, end - at), CultureInfo.InvariantCulture);
        }

        /// <summary>path가 가리키는 대괄호 구간의 정수를 전부, 나온 순서대로.</summary>
        public static long[] LongArray(string s, params string[] path)
        {
            var (from, to) = Bracket(s, Seek(s, path));
            return Longs(s, from, to);
        }

        /// <summary>path가 가리키는 대괄호 구간의 실수를 전부, 나온 순서대로. 중첩은 무시하고 편다.</summary>
        public static float[] FloatArray(string s, params string[] path)
        {
            var (from, to) = Bracket(s, Seek(s, path));
            int n = Count(s, from, to);
            var result = new float[n];
            int i = 0;
            foreach (var (a, b) in Tokens(s, from, to))
                result[i++] = float.Parse(s.Substring(a, b - a), CultureInfo.InvariantCulture);
            return result;
        }

        public static long[] Longs(string s, int from, int to)
        {
            var result = new long[Count(s, from, to)];
            int i = 0;
            foreach (var (a, b) in Tokens(s, from, to))
                result[i++] = long.Parse(s.Substring(a, b - a), CultureInfo.InvariantCulture);
            return result;
        }

        /// <summary>키를 순서대로 찾아 내려간다. 같은 이름의 키가 여러 번 나와도 앞 키 뒤부터 찾으므로 안 헷갈린다.</summary>
        private static int Seek(string s, string[] path)
        {
            int at = 0;
            foreach (var key in path)
            {
                at = s.IndexOf("\"" + key + "\"", at, StringComparison.Ordinal);
                if (at < 0) throw new FormatException($"JSON에 키가 없다: {string.Join(".", path)}");
                at += key.Length + 2;
            }
            return at;
        }

        /// <summary>at 뒤 첫 '['부터 짝이 맞는 ']'까지.</summary>
        private static (int from, int to) Bracket(string s, int at)
        {
            int from = s.IndexOf('[', at);
            if (from < 0) throw new FormatException("배열이 없다");

            int depth = 0;
            for (int i = from; i < s.Length; i++)
            {
                if (s[i] == '[') depth++;
                else if (s[i] == ']' && --depth == 0) return (from, i);
            }
            throw new FormatException("대괄호 짝이 안 맞는다");
        }

        // 두 번 훑는다 - 한 번은 개수를 세고 한 번은 읽는다. List<float>로 받으면
        // 30만 개짜리 스타일 파일에서 재할당이 계속 일어난다.
        private static int Count(string s, int from, int to)
        {
            int n = 0;
            foreach (var _ in Tokens(s, from, to)) n++;
            return n;
        }

        private static IEnumerable<(int, int)> Tokens(string s, int from, int to)
        {
            int i = from;
            while (i < to)
            {
                if (!IsNum(s[i])) { i++; continue; }
                int a = i;
                while (i < to && IsNum(s[i])) i++;
                yield return (a, i);
            }
        }

        // 'e'/'E'는 지수 표기(1.5e-05), '+'/'-'는 부호. 숫자 밖에서는 안 나오는 문자들이다.
        private static bool IsNum(char c)
            => (c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E';
    }

    // Available languages for multilingual TTS
    public static class Languages
    {
        public static readonly string[] Available = { "en", "ko", "ja", "ar", "bg", "cs", "da", "de", "el", "es", "et", "fi", "fr", "hi", "hr", "hu", "id", "it", "lt", "lv", "nl", "pl", "pt", "ro", "ru", "sk", "sl", "sv", "tr", "uk", "vi", "na" };
    }

    // ============================================================================
    // Configuration classes
    // ============================================================================

    public class Config
    {
        public AEConfig AE { get; set; } = null!;
        public TTLConfig TTL { get; set; } = null!;

        public class AEConfig
        {
            public int SampleRate { get; set; }
            public int BaseChunkSize { get; set; }
        }

        public class TTLConfig
        {
            public int ChunkCompressFactor { get; set; }
            public int LatentDim { get; set; }
        }
    }

    // ============================================================================
    // Style class
    // ============================================================================

    public class Style
    {
        public float[] Ttl { get; set; }
        public long[] TtlShape { get; set; }
        public float[] Dp { get; set; }
        public long[] DpShape { get; set; }

        public Style(float[] ttl, long[] ttlShape, float[] dp, long[] dpShape)
        {
            Ttl = ttl;
            TtlShape = ttlShape;
            Dp = dp;
            DpShape = dpShape;
        }
    }

    // ============================================================================
    // Unicode text processor
    // ============================================================================

    public class UnicodeProcessor
    {
        private readonly Dictionary<int, long> _indexer;

        public UnicodeProcessor(string unicodeIndexerPath)
        {
            var json = File.ReadAllText(unicodeIndexerPath);
            var indexerArray = MiniJson.Longs(json, 0, json.Length);
            _indexer = new Dictionary<int, long>();
            for (int i = 0; i < indexerArray.Length; i++)
            {
                _indexer[i] = indexerArray[i];
            }
        }

        private static string RemoveEmojis(string text)
        {
            var result = new StringBuilder();
            for (int i = 0; i < text.Length; i++)
            {
                int codePoint;
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    // Get the full code point from surrogate pair
                    codePoint = char.ConvertToUtf32(text[i], text[i + 1]);
                    i++; // Skip the low surrogate
                }
                else
                {
                    codePoint = text[i];
                }

                // Check if code point is in emoji ranges
                bool isEmoji = (codePoint >= 0x1F600 && codePoint <= 0x1F64F) ||
                               (codePoint >= 0x1F300 && codePoint <= 0x1F5FF) ||
                               (codePoint >= 0x1F680 && codePoint <= 0x1F6FF) ||
                               (codePoint >= 0x1F700 && codePoint <= 0x1F77F) ||
                               (codePoint >= 0x1F780 && codePoint <= 0x1F7FF) ||
                               (codePoint >= 0x1F800 && codePoint <= 0x1F8FF) ||
                               (codePoint >= 0x1F900 && codePoint <= 0x1F9FF) ||
                               (codePoint >= 0x1FA00 && codePoint <= 0x1FA6F) ||
                               (codePoint >= 0x1FA70 && codePoint <= 0x1FAFF) ||
                               (codePoint >= 0x2600 && codePoint <= 0x26FF) ||
                               (codePoint >= 0x2700 && codePoint <= 0x27BF) ||
                               (codePoint >= 0x1F1E6 && codePoint <= 0x1F1FF);

                if (!isEmoji)
                {
                    if (codePoint > 0xFFFF)
                    {
                        // Add back as surrogate pair
                        result.Append(char.ConvertFromUtf32(codePoint));
                    }
                    else
                    {
                        result.Append((char)codePoint);
                    }
                }
            }
            return result.ToString();
        }

        private string PreprocessText(string text, string lang)
        {
            // TODO: Need advanced normalizer for better performance
            text = text.Normalize(NormalizationForm.FormKD);

            // Remove emojis (wide Unicode range)
            // C# doesn't support \u{...} syntax in regex, so we use character filtering instead
            text = RemoveEmojis(text);

            // Replace various dashes and symbols
            var replacements = new Dictionary<string, string>
            {
                {"–", "-"},      // en dash
                {"‑", "-"},      // non-breaking hyphen
                {"—", "-"},      // em dash
                {"_", " "},      // underscore
                {"\u201C", "\""},     // left double quote
                {"\u201D", "\""},     // right double quote
                {"\u2018", "'"},      // left single quote
                {"\u2019", "'"},      // right single quote
                {"´", "'"},      // acute accent
                {"`", "'"},      // grave accent
                {"[", " "},      // left bracket
                {"]", " "},      // right bracket
                {"|", " "},      // vertical bar
                {"/", " "},      // slash
                {"#", " "},      // hash
                {"→", " "},      // right arrow
                {"←", " "},      // left arrow
            };

            foreach (var kvp in replacements)
            {
                text = text.Replace(kvp.Key, kvp.Value);
            }

            // Remove special symbols
            text = Regex.Replace(text, @"[♥☆♡©\\]", "");

            // Replace known expressions
            var exprReplacements = new Dictionary<string, string>
            {
                {"@", " at "},
                {"e.g.,", "for example, "},
                {"i.e.,", "that is, "},
            };

            foreach (var kvp in exprReplacements)
            {
                text = text.Replace(kvp.Key, kvp.Value);
            }

            // Fix spacing around punctuation
            text = Regex.Replace(text, @" ,", ",");
            text = Regex.Replace(text, @" \.", ".");
            text = Regex.Replace(text, @" !", "!");
            text = Regex.Replace(text, @" \?", "?");
            text = Regex.Replace(text, @" ;", ";");
            text = Regex.Replace(text, @" :", ":");
            text = Regex.Replace(text, @" '", "'");

            // Remove duplicate quotes
            while (text.Contains("\"\""))
            {
                text = text.Replace("\"\"", "\"");
            }
            while (text.Contains("''"))
            {
                text = text.Replace("''", "'");
            }
            while (text.Contains("``"))
            {
                text = text.Replace("``", "`");
            }

            // Remove extra spaces
            text = Regex.Replace(text, @"\s+", " ").Trim();

            // If text doesn't end with punctuation, quotes, or closing brackets, add a period
            if (!Regex.IsMatch(text, @"[.!?;:,'\u0022\u201C\u201D\u2018\u2019)\]}…。」』】〉》›»]$"))
            {
                text += ".";
            }

            // Validate language
            if (!Languages.Available.Contains(lang))
            {
                throw new ArgumentException($"Invalid language: {lang}. Available: {string.Join(", ", Languages.Available)}");
            }

            // Wrap text with language tags
            text = $"<{lang}>" + text + $"</{lang}>";

            return text;
        }

        private int[] TextToUnicodeValues(string text)
        {
            return text.Select(c => (int)c).ToArray();
        }

        private float[][][] GetTextMask(long[] textIdsLengths)
        {
            return Helper.LengthToMask(textIdsLengths);
        }

        public (long[][] textIds, float[][][] textMask) Call(List<string> textList, List<string> langList)
        {
            var processedTexts = textList.Select((t, i) => PreprocessText(t, langList[i])).ToList();
            var textIdsLengths = processedTexts.Select(t => (long)t.Length).ToArray();
            long maxLen = textIdsLengths.Max();

            var textIds = new long[textList.Count][];
            for (int i = 0; i < processedTexts.Count; i++)
            {
                textIds[i] = new long[maxLen];
                var unicodeVals = TextToUnicodeValues(processedTexts[i]);
                for (int j = 0; j < unicodeVals.Length; j++)
                {
                    if (_indexer.TryGetValue(unicodeVals[j], out long val))
                    {
                        textIds[i][j] = val;
                    }
                }
            }

            var textMask = GetTextMask(textIdsLengths);
            return (textIds, textMask);
        }
    }

    // ============================================================================
    // TextToSpeech class
    // ============================================================================

    public class TextToSpeech
    {
        private readonly Config _cfgs;
        private readonly UnicodeProcessor _textProcessor;
        private readonly InferenceSession _dpOrt;
        private readonly InferenceSession _textEncOrt;
        private readonly InferenceSession _vectorEstOrt;
        private readonly InferenceSession _vocoderOrt;
        public readonly int SampleRate;
        private readonly int _baseChunkSize;
        private readonly int _chunkCompressFactor;
        private readonly int _ldim;

        public TextToSpeech(
            Config cfgs,
            UnicodeProcessor textProcessor,
            InferenceSession dpOrt,
            InferenceSession textEncOrt,
            InferenceSession vectorEstOrt,
            InferenceSession vocoderOrt)
        {
            _cfgs = cfgs;
            _textProcessor = textProcessor;
            _dpOrt = dpOrt;
            _textEncOrt = textEncOrt;
            _vectorEstOrt = vectorEstOrt;
            _vocoderOrt = vocoderOrt;
            SampleRate = cfgs.AE.SampleRate;
            _baseChunkSize = cfgs.AE.BaseChunkSize;
            _chunkCompressFactor = cfgs.TTL.ChunkCompressFactor;
            _ldim = cfgs.TTL.LatentDim;
        }

        private (float[][][] noisyLatent, float[][][] latentMask) SampleNoisyLatent(float[] duration)
        {
            int bsz = duration.Length;
            float wavLenMax = duration.Max() * SampleRate;
            var wavLengths = duration.Select(d => (long)(d * SampleRate)).ToArray();
            int chunkSize = _baseChunkSize * _chunkCompressFactor;
            int latentLen = (int)((wavLenMax + chunkSize - 1) / chunkSize);
            int latentDim = _ldim * _chunkCompressFactor;

            // Generate random noise
            var random = new Random();
            var noisyLatent = new float[bsz][][];
            for (int b = 0; b < bsz; b++)
            {
                noisyLatent[b] = new float[latentDim][];
                for (int d = 0; d < latentDim; d++)
                {
                    noisyLatent[b][d] = new float[latentLen];
                    for (int t = 0; t < latentLen; t++)
                    {
                        // Box-Muller transform for normal distribution
                        double u1 = 1.0 - random.NextDouble();
                        double u2 = 1.0 - random.NextDouble();
                        noisyLatent[b][d][t] = (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
                    }
                }
            }

            var latentMask = Helper.GetLatentMask(wavLengths, _baseChunkSize, _chunkCompressFactor);

            // Apply mask
            for (int b = 0; b < bsz; b++)
            {
                for (int d = 0; d < latentDim; d++)
                {
                    for (int t = 0; t < latentLen; t++)
                    {
                        noisyLatent[b][d][t] *= latentMask[b][0][t];
                    }
                }
            }

            return (noisyLatent, latentMask);
        }

        private (float[] wav, float[] duration) _Infer(List<string> textList, List<string> langList, Style style, int totalStep, float speed = 1.05f)
        {
            int bsz = textList.Count;
            if (bsz != style.TtlShape[0])
            {
                throw new ArgumentException("Number of texts must match number of style vectors");
            }

            // Process text
            var (textIds, textMask) = _textProcessor.Call(textList, langList);
            var textIdsShape = new long[] { bsz, textIds[0].Length };
            var textMaskShape = new long[] { bsz, 1, textMask[0][0].Length };

            var textIdsTensor = Helper.IntArrayToTensor(textIds, textIdsShape);
            var textMaskTensor = Helper.ArrayToTensor(textMask, textMaskShape);

            var styleTtlTensor = new DenseTensor<float>(style.Ttl, style.TtlShape.Select(x => (int)x).ToArray());
            var styleDpTensor = new DenseTensor<float>(style.Dp, style.DpShape.Select(x => (int)x).ToArray());

            // Run duration predictor
            var dpInputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("text_ids", textIdsTensor),
                NamedOnnxValue.CreateFromTensor("style_dp", styleDpTensor),
                NamedOnnxValue.CreateFromTensor("text_mask", textMaskTensor)
            };
            using var dpOutputs = _dpOrt.Run(dpInputs);
            var durOnnx = dpOutputs.First(o => o.Name == "duration").AsTensor<float>().ToArray();
            
            // Apply speed factor to duration
            for (int i = 0; i < durOnnx.Length; i++)
            {
                durOnnx[i] /= speed;
            }

            // Run text encoder
            var textEncInputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("text_ids", textIdsTensor),
                NamedOnnxValue.CreateFromTensor("style_ttl", styleTtlTensor),
                NamedOnnxValue.CreateFromTensor("text_mask", textMaskTensor)
            };
            using var textEncOutputs = _textEncOrt.Run(textEncInputs);
            var textEmbTensor = textEncOutputs.First(o => o.Name == "text_emb").AsTensor<float>();

            // Sample noisy latent
            var (xt, latentMask) = SampleNoisyLatent(durOnnx);
            var latentShape = new long[] { bsz, xt[0].Length, xt[0][0].Length };
            var latentMaskShape = new long[] { bsz, 1, latentMask[0][0].Length };

            var totalStepArray = Enumerable.Repeat((float)totalStep, bsz).ToArray();

            // Iterative denoising
            for (int step = 0; step < totalStep; step++)
            {
                var currentStepArray = Enumerable.Repeat((float)step, bsz).ToArray();

                var vectorEstInputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor("noisy_latent", Helper.ArrayToTensor(xt, latentShape)),
                    NamedOnnxValue.CreateFromTensor("text_emb", textEmbTensor),
                    NamedOnnxValue.CreateFromTensor("style_ttl", styleTtlTensor),
                    NamedOnnxValue.CreateFromTensor("text_mask", textMaskTensor),
                    NamedOnnxValue.CreateFromTensor("latent_mask", Helper.ArrayToTensor(latentMask, latentMaskShape)),
                    NamedOnnxValue.CreateFromTensor("total_step", new DenseTensor<float>(totalStepArray, new int[] { bsz })),
                    NamedOnnxValue.CreateFromTensor("current_step", new DenseTensor<float>(currentStepArray, new int[] { bsz }))
                };

                using var vectorEstOutputs = _vectorEstOrt.Run(vectorEstInputs);
                var denoisedLatent = vectorEstOutputs.First(o => o.Name == "denoised_latent").AsTensor<float>();

                // Update xt
                int idx = 0;
                for (int b = 0; b < bsz; b++)
                {
                    for (int d = 0; d < xt[b].Length; d++)
                    {
                        for (int t = 0; t < xt[b][d].Length; t++)
                        {
                            xt[b][d][t] = denoisedLatent.GetValue(idx++);
                        }
                    }
                }
            }

            // Run vocoder
            var vocoderInputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("latent", Helper.ArrayToTensor(xt, latentShape))
            };
            using var vocoderOutputs = _vocoderOrt.Run(vocoderInputs);
            var wavTensor = vocoderOutputs.First(o => o.Name == "wav_tts").AsTensor<float>();

            return (wavTensor.ToArray(), durOnnx);
        }

        public (float[] wav, float[] duration) Call(string text, string lang, Style style, int totalStep, float speed = 1.05f, float silenceDuration = 0.3f)
        {
            if (style.TtlShape[0] != 1)
            {
                throw new ArgumentException("Single speaker text to speech only supports single style");
            }

            int maxLen = (lang == "ko" || lang == "ja") ? 120 : 300;
            var textList = Helper.ChunkText(text, maxLen);
            var wavCat = new List<float>();
            float durCat = 0.0f;

            foreach (var chunk in textList)
            {
                var (wav, duration) = _Infer(new List<string> { chunk }, new List<string> { lang }, style, totalStep, speed);

                if (wavCat.Count == 0)
                {
                    wavCat.AddRange(wav);
                    durCat = duration[0];
                }
                else
                {
                    int silenceLen = (int)(silenceDuration * SampleRate);
                    var silence = new float[silenceLen];
                    wavCat.AddRange(silence);
                    wavCat.AddRange(wav);
                    durCat += duration[0] + silenceDuration;
                }
            }

            return (wavCat.ToArray(), new float[] { durCat });
        }

        public (float[] wav, float[] duration) Batch(List<string> textList, List<string> langList, Style style, int totalStep, float speed = 1.05f)
        {
            return _Infer(textList, langList, style, totalStep, speed);
        }
    }

    // ============================================================================
    // Helper class with utility functions
    // ============================================================================

    public static class Helper
    {
        // ============================================================================
        // Utility functions
        // ============================================================================

        public static float[][][] LengthToMask(long[] lengths, long maxLen = -1)
        {
            if (maxLen == -1)
            {
                maxLen = lengths.Max();
            }

            var mask = new float[lengths.Length][][];
            for (int i = 0; i < lengths.Length; i++)
            {
                mask[i] = new float[1][];
                mask[i][0] = new float[maxLen];
                for (int j = 0; j < maxLen; j++)
                {
                    mask[i][0][j] = j < lengths[i] ? 1.0f : 0.0f;
                }
            }
            return mask;
        }

        public static float[][][] GetLatentMask(long[] wavLengths, int baseChunkSize, int chunkCompressFactor)
        {
            int latentSize = baseChunkSize * chunkCompressFactor;
            var latentLengths = wavLengths.Select(len => (len + latentSize - 1) / latentSize).ToArray();
            return LengthToMask(latentLengths);
        }

        // ============================================================================
        // ONNX model loading
        // ============================================================================

        public static InferenceSession LoadOnnx(string onnxPath, SessionOptions opts)
        {
            return new InferenceSession(onnxPath, opts);
        }

        public static (InferenceSession dp, InferenceSession textEnc, InferenceSession vectorEst, InferenceSession vocoder) 
            LoadOnnxAll(string onnxDir, SessionOptions opts)
        {
            var dpPath = Path.Combine(onnxDir, "duration_predictor.onnx");
            var textEncPath = Path.Combine(onnxDir, "text_encoder.onnx");
            var vectorEstPath = Path.Combine(onnxDir, "vector_estimator.onnx");
            var vocoderPath = Path.Combine(onnxDir, "vocoder.onnx");

            return (
                LoadOnnx(dpPath, opts),
                LoadOnnx(textEncPath, opts),
                LoadOnnx(vectorEstPath, opts),
                LoadOnnx(vocoderPath, opts)
            );
        }

        // ============================================================================
        // Configuration loading
        // ============================================================================

        public static Config LoadCfgs(string onnxDir)
        {
            var cfgPath = Path.Combine(onnxDir, "tts.json");
            var json = File.ReadAllText(cfgPath);
            
            return new Config
            {
                AE = new Config.AEConfig
                {
                    SampleRate = MiniJson.Int(json, "ae", "sample_rate"),
                    BaseChunkSize = MiniJson.Int(json, "ae", "base_chunk_size")
                },
                TTL = new Config.TTLConfig
                {
                    ChunkCompressFactor = MiniJson.Int(json, "ttl", "chunk_compress_factor"),
                    LatentDim = MiniJson.Int(json, "ttl", "latent_dim")
                }
            };
        }

        public static UnicodeProcessor LoadTextProcessor(string onnxDir)
        {
            var unicodeIndexerPath = Path.Combine(onnxDir, "unicode_indexer.json");
            return new UnicodeProcessor(unicodeIndexerPath);
        }

        // ============================================================================
        // Voice style loading
        // ============================================================================

        public static Style LoadVoiceStyle(List<string> voiceStylePaths, bool verbose = false)
        {
            int bsz = voiceStylePaths.Count;
            
            // Read first file to get dimensions
            var firstJson = File.ReadAllText(voiceStylePaths[0]);

            var ttlDims = MiniJson.LongArray(firstJson, "style_ttl", "dims");
            var dpDims = MiniJson.LongArray(firstJson, "style_dp", "dims");
            
            long ttlDim1 = ttlDims[1];
            long ttlDim2 = ttlDims[2];
            long dpDim1 = dpDims[1];
            long dpDim2 = dpDims[2];
            
            // Pre-allocate arrays with full batch size
            int ttlSize = (int)(bsz * ttlDim1 * ttlDim2);
            int dpSize = (int)(bsz * dpDim1 * dpDim2);
            var ttlFlat = new float[ttlSize];
            var dpFlat = new float[dpSize];
            
            // Fill in the data
            for (int i = 0; i < bsz; i++)
            {
                var json = File.ReadAllText(voiceStylePaths[i]);

                // 중첩 배열을 객체로 만들었다가 다시 펼 이유가 없다 - 어차피 1차원으로 쓴다.
                var ttlDataFlat = MiniJson.FloatArray(json, "style_ttl", "data");
                var dpDataFlat = MiniJson.FloatArray(json, "style_dp", "data");

                Array.Copy(ttlDataFlat, 0, ttlFlat, (int)(i * ttlDim1 * ttlDim2), ttlDataFlat.Length);
                Array.Copy(dpDataFlat, 0, dpFlat, (int)(i * dpDim1 * dpDim2), dpDataFlat.Length);
            }

            var ttlShape = new long[] { bsz, ttlDim1, ttlDim2 };
            var dpShape = new long[] { bsz, dpDim1, dpDim2 };

            if (verbose)
            {
                UnityEngine.Debug.Log($"Loaded {bsz} voice styles");
            }

            return new Style(ttlFlat, ttlShape, dpFlat, dpShape);
        }

        // ============================================================================
        // TextToSpeech loading
        // ============================================================================

        public static TextToSpeech LoadTextToSpeech(string onnxDir, bool useGpu = false)
        {
            var opts = new SessionOptions();
            if (useGpu)
            {
                throw new NotImplementedException("GPU mode is not supported yet");
            }
            else
            {
                UnityEngine.Debug.Log("Using CPU for inference");
            }

            var cfgs = LoadCfgs(onnxDir);
            var (dpOrt, textEncOrt, vectorEstOrt, vocoderOrt) = LoadOnnxAll(onnxDir, opts);
            var textProcessor = LoadTextProcessor(onnxDir);

            return new TextToSpeech(cfgs, textProcessor, dpOrt, textEncOrt, vectorEstOrt, vocoderOrt);
        }

        // ============================================================================
        // WAV file writing
        // ============================================================================

        public static void WriteWavFile(string filename, float[] audioData, int sampleRate)
        {
            using var writer = new BinaryWriter(File.Open(filename, FileMode.Create));

            int numChannels = 1;
            int bitsPerSample = 16;
            int byteRate = sampleRate * numChannels * bitsPerSample / 8;
            short blockAlign = (short)(numChannels * bitsPerSample / 8);
            int dataSize = audioData.Length * bitsPerSample / 8;

            // RIFF header
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + dataSize);
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));

            // fmt chunk
            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16); // fmt chunk size
            writer.Write((short)1); // audio format (PCM)
            writer.Write((short)numChannels);
            writer.Write(sampleRate);
            writer.Write(byteRate);
            writer.Write(blockAlign);
            writer.Write((short)bitsPerSample);

            // data chunk
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(dataSize);

            // Write audio data
            foreach (var sample in audioData)
            {
                float clamped = Math.Max(-1.0f, Math.Min(1.0f, sample));
                short intSample = (short)(clamped * 32767);
                writer.Write(intSample);
            }
        }

        // ============================================================================
        // Tensor conversion utilities
        // ============================================================================

        public static DenseTensor<float> ArrayToTensor(float[][][] array, long[] dims)
        {
            var flat = new List<float>();
            foreach (var batch in array)
            {
                foreach (var row in batch)
                {
                    flat.AddRange(row);
                }
            }
            return new DenseTensor<float>(flat.ToArray(), dims.Select(x => (int)x).ToArray());
        }

        public static DenseTensor<long> IntArrayToTensor(long[][] array, long[] dims)
        {
            var flat = new List<long>();
            foreach (var row in array)
            {
                flat.AddRange(row);
            }
            return new DenseTensor<long>(flat.ToArray(), dims.Select(x => (int)x).ToArray());
        }

        // ============================================================================
        // Timer utility
        // ============================================================================

        public static T Timer<T>(string name, Func<T> func)
        {
            var start = DateTime.Now;
            UnityEngine.Debug.Log($"{name}...");
            var result = func();
            var elapsed = (DateTime.Now - start).TotalSeconds;
            UnityEngine.Debug.Log($"  -> {name} completed in {elapsed:F2} sec");
            return result;
        }

        public static string SanitizeFilename(string text, int maxLen)
        {
            var result = new StringBuilder();
            int count = 0;
            foreach (char c in text)
            {
                if (count >= maxLen) break;
                if (char.IsLetterOrDigit(c))
                {
                    result.Append(c);
                }
                else
                {
                    result.Append('_');
                }
                count++;
            }
            return result.ToString();
        }

        // ============================================================================
        // Chunk text
        // ============================================================================

        public static List<string> ChunkText(string text, int maxLen = 300)
        {
            var chunks = new List<string>();

            // Split by paragraph (two or more newlines)
            var paragraphRegex = new Regex(@"\n\s*\n+");
            var paragraphs = paragraphRegex.Split(text.Trim())
                .Select(p => p.Trim())
                .Where(p => !string.IsNullOrEmpty(p))
                .ToList();

            // Split by sentence boundaries, excluding abbreviations
            var sentenceRegex = new Regex(@"(?<!Mr\.|Mrs\.|Ms\.|Dr\.|Prof\.|Sr\.|Jr\.|Ph\.D\.|etc\.|e\.g\.|i\.e\.|vs\.|Inc\.|Ltd\.|Co\.|Corp\.|St\.|Ave\.|Blvd\.)(?<!\b[A-Z]\.)(?<=[.!?])\s+");

            foreach (var paragraph in paragraphs)
            {
                var sentences = sentenceRegex.Split(paragraph);
                string currentChunk = "";

                foreach (var sentence in sentences)
                {
                    if (string.IsNullOrEmpty(sentence)) continue;

                    if (currentChunk.Length + sentence.Length + 1 <= maxLen)
                    {
                        if (!string.IsNullOrEmpty(currentChunk))
                        {
                            currentChunk += " ";
                        }
                        currentChunk += sentence;
                    }
                    else
                    {
                        if (!string.IsNullOrEmpty(currentChunk))
                        {
                            chunks.Add(currentChunk.Trim());
                        }
                        currentChunk = sentence;
                    }
                }

                if (!string.IsNullOrEmpty(currentChunk))
                {
                    chunks.Add(currentChunk.Trim());
                }
            }

            // If no chunks were created, return the original text
            if (chunks.Count == 0)
            {
                chunks.Add(text.Trim());
            }

            return chunks;
        }
    }
}
