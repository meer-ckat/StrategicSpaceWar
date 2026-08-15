using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;

/// <summary>
/// 효과음 재생. 이름으로 클립을 찾아 놀고 있는 AudioSource에 꽂아 재생한다.
///
/// 반납 코드는 없다 - 노는 소스는 `!isPlaying`인 소스다. 별도 플래그를 두면 그 플래그와
/// 실제 재생 상태가 어긋나는 순간부터 소리가 영영 안 나기 시작하는데, 원인을 찾기 어렵다.
///
/// 루프 사운드(엔진음 등)는 아직 지원하지 않는다. 필요해지면 그때 핸들을 돌려주는
/// 별도 API로 붙일 것 - 여기 섞으면 위의 "노는 소스 = 안 울리는 소스"가 깨진다.
/// </summary>
public sealed class SoundManager : MonoBehaviour
{
    private const string Folder = "Sound/";

    /// <summary>
    /// 클립별 기본 음량. 부르는 쪽이 넘긴 volume에 곱해진다.
    ///
    /// 여기 있는 이유: "이 소리가 얼마나 큰가"는 부르는 쪽의 사정이 아니라 클립의 성질이다.
    /// 호출부마다 0.3f를 뿌려 놓으면 균형을 다시 잡을 때 파일 다섯 개를 뒤져야 하고,
    /// 새로 부르는 자리가 생길 때마다 그 숫자를 잊는다.
    ///
    /// 균형의 요점은 **유폭이 안 묻히는 것**이다. 관통·발사·도탄은 초당 수십 번 나는 일상음이고
    /// 유폭은 배 한 척이 사라지는 사건인데, 둘이 같은 음량이면 큰 쪽이 잔소리에 파묻힌다.
    /// 표에 없는 이름은 1.0이다.
    /// </summary>
    private static readonly Dictionary<string, float> BaseVolume = new()
    {
        { "Cannon", 0.30f },      // 초당 5발까지 난다
        { "Penetrate", 0.35f },
        { "Blocked", 0.25f },     // 아무 일도 안 일어난 소리다. 제일 작아도 된다
        { "Ricochet", 0.30f },
        { "Blow", 0.45f },        // 계속 나는 배경음에 가깝다
        { "Breakaway", 0.85f },
        { "Explosion", 1.00f },   // 기준점
    };

    /// <summary>동시에 울릴 수 있는 소리의 수. 스폴 한 번에 파편이 24개 날아간다.</summary>
    [Header("Pool")]
    [SerializeField] private int maxVoices = 24;

    [Header("3D")]
    [SerializeField] private float minDistance = 3f;
    [SerializeField] private float maxDistance = 40f;

    /// <summary>비워두면 기본 출력으로 나간다. 나중에 SFX/BGM 볼륨을 나눌 때 쓸 자리.</summary>
    [SerializeField] private AudioMixerGroup mixerGroup;

    private static SoundManager _instance;

    private readonly Dictionary<string, AudioClip> _clips = new();
    private readonly Dictionary<AudioClip, int> _lastFrame = new();
    private readonly List<AudioSource> _pool = new();

    /// <summary>2D. 화면 어디서 터지든 같은 크기로 들린다.</summary>
    public static void AudioShot(string clipName, float volume = 1f, float pitch = 1f)
        => Play(clipName, null, volume, pitch);

    /// <summary>그 지점에서 난다. 카메라에서 멀면 작게 들린다.</summary>
    public static void AudioShot(
        string clipName, Vector2 position, float volume = 1f, float pitch = 1f)
        => Play(clipName, position, volume, pitch);

    // 씬에 아무것도 안 붙여도 돌아야 한다. SpallTrails와 같은 이유, 같은 방식.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Boot()
    {
        if (_instance != null)
            return;

        var go = new GameObject(nameof(SoundManager));
        DontDestroyOnLoad(go);
        go.AddComponent<SoundManager>();
    }

    private void Awake()
    {
        // 씬에 손으로 하나 더 놔뒀더라도 소리가 두 번 나면 안 된다.
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }

        _instance = this;
    }

    private void OnDestroy()
    {
        if (_instance == this)
            _instance = null;
    }

    private static void Play(string clipName, Vector2? position, float volume, float pitch)
    {
        // 에디터 테스트처럼 씬이 없는 자리에서 불릴 수 있다. 소리는 없어도 그만이다.
        if (_instance == null || string.IsNullOrEmpty(clipName))
            return;

        _instance.PlayInternal(clipName, position, volume, pitch);
    }

    private void PlayInternal(string clipName, Vector2? position, float volume, float pitch)
    {
        AudioClip clip = Resolve(clipName);

        if (clip == null)
            return;

        // 파편 24개가 같은 판을 동시에 때리면 똑같은 소리가 24겹으로 쌓인다. 볼륨만
        // 터지고 위상 간섭으로 지저분해질 뿐, 24배로 들리지 않는다.
        if (_lastFrame.TryGetValue(clip, out int frame) && frame == Time.frameCount)
            return;

        _lastFrame[clip] = Time.frameCount;

        AudioSource source = Borrow();

        if (source == null)
            return;

        source.transform.position = position ?? Vector3.zero;
        source.spatialBlend = position.HasValue ? 1f : 0f;
        source.clip = clip;
        source.volume = volume * (BaseVolume.TryGetValue(clipName, out float baseVolume)
            ? baseVolume
            : 1f);
        source.pitch = pitch;
        source.Play();
    }

    private AudioClip Resolve(string clipName)
    {
        if (_clips.TryGetValue(clipName, out AudioClip cached))
            return cached;

        AudioClip clip = Resources.Load<AudioClip>(Folder + clipName);

        // 못 찾은 것도 캐시한다. 안 그러면 오타 하나가 파편마다 디스크를 뒤진다.
        _clips[clipName] = clip;

        if (clip == null)
            Debug.LogWarning($"SoundManager: Resources/{Folder}{clipName} 없음.");

        return clip;
    }

    /// <summary>놀고 있는 소스. 없으면 상한까지 새로 만든다. 상한을 넘으면 이번 소리는 버린다.</summary>
    private AudioSource Borrow()
    {
        for (int i = 0; i < _pool.Count; i++)
        {
            if (!_pool[i].isPlaying)
                return _pool[i];
        }

        if (_pool.Count >= maxVoices)
            return null;

        var go = new GameObject($"Voice {_pool.Count}");
        go.transform.SetParent(transform, false);

        var source = go.AddComponent<AudioSource>();

        source.playOnAwake = false;
        source.outputAudioMixerGroup = mixerGroup;
        source.rolloffMode = AudioRolloffMode.Linear;
        source.minDistance = minDistance;
        source.maxDistance = maxDistance;

        _pool.Add(source);

        return source;
    }
}
