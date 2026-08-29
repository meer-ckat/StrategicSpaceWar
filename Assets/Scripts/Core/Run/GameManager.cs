using IMGUI;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// 게임 루프의 주인. 지금 하는 일은 하나다 - 플레이어가 전투 불능이 되면
/// 격파 시퀀스를 돌리고 씬을 처음부터 다시 연다.
///
/// 시퀀스의 시계가 여기 하나뿐인 것이 요점이다. HUD 소등(ShipStatusHud),
/// 다른 GUI의 즉시 꺼짐(Dialogue·Contact·HitIndicator), 암전, 재시작이 전부
/// <see cref="DownSeconds"/> 하나에서 파생된다 - 시계가 둘이면 "모든 HUD가
/// 꺼진 뒤"라는 시점이 어긋난다.
/// </summary>
public sealed class GameManager : MonoBehaviour
{
    /// <summary>플레이어가 전투 불능인가. 다른 GUI들이 이걸 보고 즉시 꺼진다.</summary>
    public static bool PlayerDown { get; private set; }

    /// <summary>전투 불능 뒤 흐른 시간(초). 살아 있으면 음수 - HUD 소등 시각표의 원점.</summary>
    public static float DownSeconds =>
        PlayerDown ? Time.unscaledTime - _downTime : -1f;

    /// <summary>씬이 열리고 흐른 시간(초). HUD 부팅 시각표의 원점.</summary>
    public static float SceneSeconds => Time.unscaledTime - _sceneStart;

    /// <summary>재시작 직후 완전한 검정을 유지하는 시간.</summary>
    private const float BootBlackHold = 1f;

    /// <summary>
    /// 부팅 연출이 시작되기까지의 지연 = 검정 유지 + 페이드인(0.5초).
    /// 화면이 다 밝아진 순간 첫 계기가 켜진다.
    /// </summary>
    public const float GuiBootDelay = BootBlackHold + 0.5f;

    /// <summary>
    /// 지금 GUI가 숨어야 하는가. 격파 중이거나, 재시작 직후 HUD 부팅이 아직 안 끝난
    /// 동안이다 - 대사창·마커·피격 표시가 전부 이 하나를 본다. 계기가 다 들어온 뒤에
    /// 전술 정보가 들어오는 순서다.
    /// </summary>
    public static bool GuiHidden =>
        PlayerDown || SceneSeconds < GuiBootDelay + ShipStatusHud.BootSpanSeconds;

    private static float _sceneStart;

    /// <summary>
    /// 플레이어 함선. 프레임당 여러 곳이 물어서 프레임 캐시 하나 - 파괴된 프레임에는
    /// 마지막 참조가 Unity fake-null이라 검사(== null)가 자연히 걸러낸다.
    /// </summary>
    public static Ship Player()
    {
        if (_playerFrame == Time.frameCount)
            return _player;

        _playerFrame = Time.frameCount;
        _player = null;

        for (int i = 0; i < Ship.All.Count; i++)
        {
            Ship ship = Ship.All[i];

            if (ship != null && ship.IsPlayerControlled)
            {
                _player = ship;
                break;
            }
        }

        return _player;
    }

    private static Ship _player;
    private static int _playerFrame = -1;
    private static float _downTime;

    // 암전: 모든 HUD가 꺼진 뒤 붉게 번쩍(DieFlashSeconds), 검정으로 페이드, 잠시 들고 재시작.
    private const float BlackFadeSeconds = 1f;
    private const float HoldBlackSeconds = 1f;

    private Image _blackout;
    private bool _blackoutMissing;
    private bool _restarting;
    private bool _sawPlayer;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install()
    {
        // 첫 씬은 sceneLoaded 콜백보다 먼저 열려 있다 - 원점을 여기서 한 번 찍는다.
        _sceneStart = Time.unscaledTime;

        if (FindFirstObjectByType<GameManager>() != null)
            return;

        var go = new GameObject("Game Manager");

        DontDestroyOnLoad(go);

        go.AddComponent<GameManager>();
    }

    private void OnEnable() => SceneManager.sceneLoaded += OnSceneLoaded;

    private void OnDisable() => SceneManager.sceneLoaded -= OnSceneLoaded;

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode) =>
        _sceneStart = Time.unscaledTime;

    private void Update()
    {
        // ImGui 수확의 주인. 뷰들(Dialogue·Contact)이 격파 게이트로 전부 침묵하면
        // Begin을 부르는 사람이 없어져 마지막 프레임 위젯이 화면에 박제된다 - 선언과
        // 무관하게 수확은 프레임마다 돌아야 한다. Begin은 프레임당 1회 가드가 있어
        // 뷰들이 또 불러도 무해하다.
        ImGui.Begin();

        Ship player = Player();

        if (player != null)
            _sawPlayer = true;

        if (!PlayerDown)
        {
            BootFade();

            // 유폭 즉사는 IsCombatEffective가 false를 스칠 틈 없이 오브젝트가 사라질 수
            // 있다 - "봤던 플레이어가 없어졌다"도 격파다. 스폰 전의 null은 _sawPlayer가
            // 걸러낸다.
            bool down =
                (player != null && !player.IsCombatEffective) ||
                (player == null && _sawPlayer);

            if (!down)
                return;

            PlayerDown = true;
            _downTime = Time.unscaledTime;
            BeginSilence();

            // 사건당 한 줄. "왜 안 꺼지지"의 답이 콘솔에 있어야 한다 - 트리거가
            // 전투 불능(승무원·전원·무장/추진)이라 오너가 보는 "죽음"과 다를 수 있다.
            Debug.Log($"[GameManager] 격파 - 시퀀스 시작 (player={(player == null ? "파괴됨" : player.name)})");
            return;
        }

        // 되살아났다 - 수리로 전투 가능이 돌아온 경우. 시퀀스를 접는다.
        if (player != null && player.IsCombatEffective)
        {
            PlayerDown = false;
            EndSilence();
            return;
        }

        float blackoutAt = ShipStatusHud.ShutdownSeconds;
        float t = DownSeconds - blackoutAt;

        if (t < 0f)
            return;

        Blackout(t);

        if (!_restarting && t >= ShipStatusHud.DieFlashSeconds + BlackFadeSeconds + HoldBlackSeconds)
        {
            _restarting = true;
            Restart();
        }
    }

    private AudioSource _tinnitus;

    /// <summary>
    /// 세계가 조용해지고 이명만 남는다. 뮤트는 AudioListener.pause라 재생 중이던
    /// 소리까지 전부 멎고, 이명 소스만 ignoreListenerPause로 그 정지를 뚫는다 -
    /// SoundManager 풀을 안 거치는 이유다: 풀 소스들은 같이 멎는 것이 목적이다.
    /// </summary>
    private void BeginSilence()
    {
        AudioListener.pause = true;

        if (_tinnitus == null)
        {
            var clip = Resources.Load<AudioClip>("Sound/Tinnitus");

            if (clip == null)
            {
                // 파일이 없어도 시퀀스는 돈다 - 조용한 죽음일 뿐이다.
                Debug.LogWarning("[GameManager] Resources/Sound/Tinnitus가 없다. 이명 없이 간다.");
                return;
            }

            _tinnitus = gameObject.AddComponent<AudioSource>();
            _tinnitus.clip = clip;
            _tinnitus.loop = true;
            _tinnitus.spatialBlend = 0f;
            _tinnitus.ignoreListenerPause = true;
        }

        _tinnitus.Play();
    }

    /// <summary>재시작 직전과 부활에 - 이명이 끊기고 세계 소리가 돌아온다.</summary>
    private void EndSilence()
    {
        AudioListener.pause = false;

        if (_tinnitus != null)
            _tinnitus.Stop();
    }

    /// <summary>t초째의 암전. 붉게 번쩍였다가 검정으로 - 소등 연출과 같은 색, 같은 박자.</summary>
    private Image FindBlackout()
    {
        if (_blackout != null || _blackoutMissing)
            return _blackout;

        GameObject engine = GameObject.Find("ScriptEngine");

        _blackout = engine != null
            ? engine.transform.Find("DramaticBackground")?.GetComponent<Image>()
            : null;

        if (_blackout == null)
        {
            // 없어도 루프는 돈다 - 연출이 빠질 뿐 재시작은 해야 한다.
            _blackoutMissing = true;
            Debug.LogWarning("[GameManager] ScriptEngine/DramaticBackground(Image)가 없다. 암전 없이 간다.");
            return null;
        }

        _blackout.gameObject.SetActive(true);
        return _blackout;
    }

    private void Blackout(float t)
    {
        Image blackout = FindBlackout();

        if (blackout == null)
            return;

        blackout.color = t < ShipStatusHud.DieFlashSeconds
            ? Color.red
            : Color.Lerp(
                Color.red,
                Color.black,
                (t - ShipStatusHud.DieFlashSeconds) / BlackFadeSeconds);
    }

    /// <summary>
    /// 재시작(그리고 첫 진입) 직후의 암전 걷기. 1초는 완전한 검정을 유지하고 - 리로드
    /// 순간의 스폰·초기화가 이 뒤에 숨는다 - 0.5초에 걸쳐 투명해진다. 합이 정확히
    /// GuiBootDelay라, 화면이 다 밝아진 순간 첫 계기(함체)가 켜진다.
    /// </summary>
    private void BootFade()
    {
        float t = SceneSeconds;

        if (t >= GuiBootDelay)
            return;

        // 이 구간에서는 이명 정지와 뮤트 해제를 매 프레임 보장한다. 정지는 멱등이라
        // 공짜고, 어떤 경로로 새어 들어온 이명이든 여기서 확실히 끊긴다.
        EndSilence();

        Image blackout = FindBlackout();

        if (blackout == null)
            return;

        float alpha = t < BootBlackHold
            ? 1f
            : 1f - (t - BootBlackHold) / (GuiBootDelay - BootBlackHold);

        blackout.color = new Color(0f, 0f, 0f, alpha);
    }

    private void Restart()
    {
        Debug.Log("[GameManager] 재시작 - 씬을 처음부터");

        EndSilence();

        // 씬을 처음부터. RunState 저장 파일은 안 건드린다 - 죽음은 저장을 만들지도
        // 지우지도 않고, 마지막으로 저장된 상태에서 다시 시작한다.
        PlayerDown = false;
        _sawPlayer = false;
        _restarting = false;
        _blackout = null;
        _blackoutMissing = false;

        SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
    }
}
