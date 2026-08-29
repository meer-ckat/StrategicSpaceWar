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
    private const float HoldBlackSeconds = 0.5f;

    private Image _blackout;
    private bool _blackoutMissing;
    private bool _restarting;
    private bool _sawPlayer;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install()
    {
        if (FindFirstObjectByType<GameManager>() != null)
            return;

        var go = new GameObject("Game Manager");

        DontDestroyOnLoad(go);

        go.AddComponent<GameManager>();
    }

    private void Update()
    {
        Ship player = Player();

        if (player != null)
            _sawPlayer = true;

        if (!PlayerDown)
        {
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
            return;
        }

        // 되살아났다 - 수리로 전투 가능이 돌아온 경우. 시퀀스를 접는다.
        if (player != null && player.IsCombatEffective)
        {
            PlayerDown = false;
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

    /// <summary>t초째의 암전. 붉게 번쩍였다가 검정으로 - 소등 연출과 같은 색, 같은 박자.</summary>
    private void Blackout(float t)
    {
        if (_blackout == null && !_blackoutMissing)
        {
            GameObject engine = GameObject.Find("ScriptEngine");

            _blackout = engine != null
                ? engine.transform.Find("DramaticBackground")?.GetComponent<Image>()
                : null;

            if (_blackout == null)
            {
                // 없어도 루프는 돈다 - 연출이 빠질 뿐 재시작은 해야 한다.
                _blackoutMissing = true;
                Debug.LogWarning("[GameManager] ScriptEngine/DramaticBackground(Image)가 없다. 암전 없이 재시작한다.");
                return;
            }

            _blackout.gameObject.SetActive(true);
        }

        if (_blackout == null)
            return;

        _blackout.color = t < ShipStatusHud.DieFlashSeconds
            ? Color.red
            : Color.Lerp(
                Color.red,
                Color.black,
                (t - ShipStatusHud.DieFlashSeconds) / BlackFadeSeconds);
    }

    private void Restart()
    {
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
