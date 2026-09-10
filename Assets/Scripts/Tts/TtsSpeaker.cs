using System.Threading.Tasks;
using UnityEngine;

namespace Supertonic
{
    /// <summary>
    /// B안: 게임이 돌아가는 중에 그 자리에서 합성해 읽는다. 미리 안 써둔 텍스트
    /// (함선 이름, 좌표, 절차생성 문장)를 읽을 수 있는 대신, 빌드에 383 MB 모델과
    /// ONNX 런타임이 실린다 - 그러면 모델을 배포하는 것이므로 OpenRAIL 4조가 붙는다
    /// (LICENSE 사본 동봉 + EULA에 사용제한 조항).
    ///
    /// 합성은 백그라운드 스레드에서 돈다. 그래도 첫 호출은 모델 로딩 때문에 수 초 걸리므로
    /// 로딩 화면에서 <see cref="Warmup"/>을 한 번 불러 미리 올려두는 편이 낫다.
    ///
    /// 붙여서 쓰는 법: 빈 GameObject에 이 컴포넌트를 얹고, 인스펙터에서 text를 채운 뒤
    /// 컨텍스트 메뉴 "말해봐"를 누른다. 플레이 중에도 된다.
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public sealed class TtsSpeaker : MonoBehaviour
    {
        [Header("시험용")]
        [SerializeField, TextArea(2, 5)]
        private string text = "접촉 전부 소실. 교전 종료.";

        [SerializeField] private string lang = "ko";
        [SerializeField] private string voice = SupertonicTts.DefaultVoice;

        [Tooltip("디노이즈 단계. 올리면 품질이 오르고 그만큼 느려진다.")]
        [SerializeField, Range(4, 16)] private int totalStep = SupertonicTts.DefaultSteps;

        private AudioSource _source;

        private void Awake() => _source = GetComponent<AudioSource>();

        /// <summary>모델을 미리 올린다. 첫 대사에서 수 초 멈추는 걸 로딩 화면으로 옮기는 용도.</summary>
        public static Task Warmup() => Task.Run(() => { var _ = SupertonicTts.SampleRate; });

        /// <summary>읽고 재생한다. 앞 대사가 아직 울리고 있으면 끊고 새 것으로 덮는다.</summary>
        public async Task SpeakAsync(string message)
        {
            var clip = await SupertonicTts.SpeakAsync(message, lang, voice, totalStep);
            if (clip == null || this == null) return;

            _source.Stop();
            _source.clip = clip;
            _source.Play();
        }

        [ContextMenu("말해봐")]
        private async void SpeakFromInspector()
        {
            if (_source == null) _source = GetComponent<AudioSource>();
            await SpeakAsync(text);
        }
    }
}
