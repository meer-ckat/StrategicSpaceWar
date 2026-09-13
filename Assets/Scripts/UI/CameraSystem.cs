using UnityEngine;
using UnityEngine.InputSystem;

public class CameraSystem : MonoBehaviour
{
    public enum TrackingType
    {
        Following,
        Centering,
        Aim
    }
    public TrackingType myType;
    public Transform A;
    public Transform B;
    public float ratio;
    Camera cam;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        cam = GetComponent<Camera>();
        _instance = this;
    }

    // --- 화면 흔들림 ---
    //
    // 그림이다. 유폭이 화면 밖에서 나도 뭔가 일어났다는 것이 전해져야 하고, 화면 안이면
    // 판이 사라지는 한 틱짜리 사건에 무게가 생긴다.

    private static CameraSystem _instance;

    private float _shake;
    private uint _shakeSeed = 1;

    /// <summary>세기만 받는다. 지속시간은 감쇠율이 정한다 - 손잡이가 둘이면 하나는 안 만진다.</summary>
    public static void Shake(float amount)
    {
        if (_instance != null)
            _instance._shake = Mathf.Max(_instance._shake, amount);
    }

    // Update is called once per frame
    void LateUpdate()
    {
        switch(myType)
        {
            case TrackingType.Centering:
            Centering();
            break;
            case TrackingType.Following:
            Following();
            break;
            case TrackingType.Aim:
            Aim();
            break;
        }
        Vector2 jitter = Vector2.zero;

        if (_shake > 0.001f)
        {
            // UnityEngine.Random 금지. 결정론 불변식은 그림에도 적용된다 - 리플레이가
            // 카메라 때문에 어긋나면 그것도 어긋난 것이다.
            var rng = new DeterministicRng(Ballistics.Hash(0, Core.TickManager.currentTick, (int)_shakeSeed++));

            jitter = new Vector2(rng.Range(-_shake, _shake), rng.Range(-_shake, _shake));

            // 프레임률과 무관하게 약 0.25초에 사그라든다.
            _shake *= Mathf.Pow(0.02f, Time.deltaTime / 0.25f);
        }
        else
        {
            _shake = 0f;
        }

        transform.position = new Vector3(
            transform.position.x + jitter.x, transform.position.y + jitter.y, -10f -cam.orthographicSize);
    }

    // --- 컷신 프레이밍 ---
    //
    // 컷신이 A/B를 잠깐 갈아끼운다. 원래 값은 여기 두었다가 끝날 때 되돌린다 -
    // 인스펙터에 적힌 것이 진짜 주인이고, 컷신은 빌려 쓰는 쪽이다.

    private bool _cutsceneHeld;
    private TrackingType _savedType;
    private Transform _savedA;
    private Transform _savedB;
    private float _savedMoveSmooth;
    private float _savedZoomSmooth;
    private float _savedRatio;
    private Vector2 _cutsceneMoveVelocity;
    private float _cutsceneZoomVelocity;
    private float _cutsceneSize;
    private const float ReturnDuration = 3f;
    private float _returnRemaining;
    private Vector2 _returnOffset;
    private float _returnSize;

    /// <summary>
    /// 따라가는 속도. 클수록 느긋하다(SmoothDamp의 시간 상수라 **작을수록 빠르다**).
    /// 연출이 "탁 붙는다"와 "천천히 흘러간다"를 가르는 손잡이가 이것 하나다.
    ///
    /// 0을 주면 SmoothDamp가 그 프레임에 목표로 순간이동한다 - 컷신 시작에서 화면을
    /// 미리 맞춰 두고 싶을 때 쓴다.
    /// </summary>
    public static void CutsceneDamp(float move, float zoom)
    {
        if (_instance == null)
            return;

        _instance.HoldForCutscene();

        _instance.moveSmooth = Mathf.Max(0f, move);
        _instance.zoomSmooth = Mathf.Max(0f, zoom);
    }

    /// <summary>
    /// 컷신이 프레임을 잡는다. b가 있으면 둘을 다 담고(Centering), 없으면 a만 따라간다.
    ///
    /// **원래 값은 처음 한 번만 저장한다.** 컷신 도중에 대상이 여러 번 바뀌는데, 매번
    /// 저장하면 두 번째 호출이 "컷신이 방금 넣은 값"을 원본으로 기억해서 영영 안 돌아온다.
    /// </summary>
    /// <param name="zoom">
    /// 둘을 담을 때 거리에 곱하는 여유. 0이면 씬 값을 그대로 쓴다. **전투 값(1.4쯤)은
    /// 컷신에 너무 크다** - 300 m 떨어진 두 배를 그 값으로 담으면 화면이 840 m 폭이라
    /// 배가 점이 된다.
    /// </param>
    public static void CutsceneFrame(Transform a, Transform b, float zoom = 0f, float size = 0f)
    {
        if (_instance == null || a == null)
            return;

        _instance.HoldForCutscene();

        _instance.A = a;
        _instance.B = b;
        _instance.myType = b != null ? TrackingType.Centering : TrackingType.Following;
        _instance._cutsceneSize = Mathf.Max(0f, size);

        if (zoom > 0f)
            _instance.ratio = zoom;
    }

    private void HoldForCutscene()
    {
        if (_cutsceneHeld)
            return;

        _cutsceneHeld = true;
        _returnRemaining = 0f;
        _savedType = myType;
        _savedA = A;
        _savedB = B;
        _savedMoveSmooth = moveSmooth;
        _savedZoomSmooth = zoomSmooth;
        _savedRatio = ratio;
        _cutsceneMoveVelocity = Vector2.zero;
        _cutsceneZoomVelocity = 0f;
        _cutsceneSize = 0f;
    }

    /// <summary>
    /// 인스펙터가 적어 둔 프레임으로 되돌린다. **컷신 배를 지우기 전에 불러야 한다** -
    /// 지운 뒤에 남은 참조로 position을 읽으면 그 자리에서 예외가 난다. 안 빌렸으면
    /// 아무 일도 안 일어난다.
    /// </summary>
    /// <param name="blend">
    /// false면 3초 복귀 블렌드 없이 그 자리에서 놓는다. 워프가 쓴다 - 암전 밑에서 배가
    /// 다른 구역으로 순간이동한 뒤라, 블렌드하면 옛 앵커에서 새 자리까지 수천 m를 팬한다.
    /// </param>
    public static void ReleaseCutscene(bool blend = true)
    {
        if (_instance == null || !_instance._cutsceneHeld)
            return;

        _instance._cutsceneHeld = false;
        _instance.myType = _instance._savedType;
        _instance.A = _instance._savedA;
        _instance.B = _instance._savedB;
        _instance.moveSmooth = _instance._savedMoveSmooth;
        _instance.zoomSmooth = _instance._savedZoomSmooth;
        _instance.ratio = _instance._savedRatio;
        _instance._returnRemaining = 0f;
        if (blend && _instance.myType == TrackingType.Aim && _instance.A != null)
        {
            _instance._returnOffset = (Vector2)(_instance.transform.position - _instance.A.position);
            _instance._returnSize = _instance.cam.orthographicSize;
            _instance._returnRemaining = ReturnDuration;
            _instance.zoomVelocity = 0f;
        }
    }

    void Following()
    {
        if (A == null)
            return;

        if (_cutsceneHeld)
            MoveCutscene(A.position, _cutsceneSize > 0f ? _cutsceneSize : cam.orthographicSize, Time.unscaledDeltaTime);
        else
            transform.position = Vector2.Lerp((Vector2)transform.position, (Vector2)A.position, 0.1f);
    }

    void Centering()
    {
        // 컷신 배가 먼저 치워지고 카메라가 한 프레임 늦게 도는 창이 있다. 둘 중 하나만
        // 죽어도 남은 쪽을 따라가는 쪽이, 예외로 프레임을 통째로 잃는 것보다 낫다.
        if (A == null || B == null)
        {
            Transform alive = A != null ? A : B;

            if (alive != null)
            {
                if (_cutsceneHeld)
                    MoveCutscene(alive.position, _cutsceneSize > 0f ? _cutsceneSize : cam.orthographicSize, Time.unscaledDeltaTime);
                else
                    transform.position = Vector2.Lerp((Vector2)transform.position, (Vector2)alive.position, 0.1f);
            }

            return;
        }

        Vector2 center = (A.position + B.position) / 2;
        float size = Mathf.Max(10f, (A.position - B.position).magnitude * ratio);
        if (_cutsceneHeld)
        {
            MoveCutscene(center, _cutsceneSize > 0f ? _cutsceneSize : size, Time.unscaledDeltaTime);
            return;
        }

        cam.orthographicSize = size;
        transform.position = Vector2.Lerp((Vector2)transform.position, center, 0.1f);
    }

    private void MoveCutscene(Vector2 target, float size, float deltaTime)
    {
        // 대본의 시계와 맞춘다. 게임 일시정지 중에도 브리핑은 흐른다.
        deltaTime = Mathf.Min(deltaTime, 0.1f);
        if (moveSmooth <= 0f)
            _cutsceneMoveVelocity = Vector2.zero;
        if (zoomSmooth <= 0f)
            _cutsceneZoomVelocity = 0f;
        Vector2 position = moveSmooth <= 0f ? target : Vector2.SmoothDamp(
            transform.position, target, ref _cutsceneMoveVelocity, moveSmooth,
            Mathf.Infinity, deltaTime);
        transform.position = new Vector3(position.x, position.y, transform.position.z);
        cam.orthographicSize = zoomSmooth <= 0f ? size : Mathf.SmoothDamp(
            cam.orthographicSize, size, ref _cutsceneZoomVelocity, zoomSmooth,
            Mathf.Infinity, deltaTime);
    }

    [SerializeField] float minZoom = 10f;
    [SerializeField] float maxZoom = 18f;

    [SerializeField] float lookAhead = 8f;

    [SerializeField] float moveSmooth = 0.15f;
    [SerializeField] float zoomSmooth = 0.15f;

    /// <summary>
    /// 속도 기반 줌아웃 배율. 위치가 하드락된 뒤로 지연 보상이 아니라 순수 연출이다 -
    /// 빠를수록 넓게 보여서 속도감을 주고, 마주 오는 것을 미리 보여준다.
    /// 식의 moveSmooth 곱은 하드락 전 튜닝 값을 그대로 보존하려고 남겨 둔 상수다.
    /// </summary>
    [SerializeField] float speedZoomFactor = 2f;

    /// <summary>속도 기반 줌의 상한. 없으면 충각·파편 따위의 순간 고속 스파이크에 화면이 무한히 넓어진다.</summary>
    [SerializeField] float maxSpeedZoom = 80f;

    /// <summary>
    /// 항해 줌. 전투 눈금(maxSpeedZoom 80 = 폭 280 m)은 전속 467 m/s에서 0.6초짜리 화면이라
    /// 들판에서는 아무것도 안 보인다. 이 속도를 넘고 **적 접촉이 없으면** 화면을 센서 지름
    /// (700 = 높이 1.4 km, 폭 2.5 km)까지 넓힌다. 접촉이 생기면 전투 눈금으로 바로 돌아온다 -
    /// 나가는 건 느리게(cruiseZoomSmooth), 돌아오는 건 zoomSmooth로. 문턱에 히스테리시스를 둔
    /// 것은 전속 근처에서 화면이 들락거리지 않게.
    /// </summary>
    [SerializeField] float cruiseZoom = 700f;
    [SerializeField] float cruiseSpeed = 100f;
    [SerializeField] float cruiseExitSpeed = 60f;
    [SerializeField] float cruiseZoomSmooth = 1.2f;

    private bool _cruising;

    private Vector2 moveVelocity;
    private float zoomVelocity;

    // 마우스 lookAhead 오프셋의 현재값. 부드러움은 이것에만 있다 - 배 위치는 하드락이다.
    private Vector2 _aimOffset;

    private Transform _rigOwner;
    private Rigidbody2D _targetRig;

    void Aim()
    {
        if (A == null)
            return;

        // 0~1
        Vector2 mouseViewport =
            Camera.main.ScreenToViewportPoint(Mouse.current.position.ReadValue());

        // 화면 중앙 기준 -1 ~ +1
        Vector2 aim =
            (mouseViewport - new Vector2(0.5f, 0.5f)) * 2f;

        // 원형 범위로 제한
        aim = Vector2.ClampMagnitude(aim, 1f);

        // 배 위치는 보간 없이 그대로 물린다. SmoothDamp에 배 위치까지 넣으면 정상
        // 이동 중 "속도 × moveSmooth"만큼 영구히 뒤처지고, 배(60Hz 틱)와 카메라
        // (프레임 시계)가 다른 시계를 타서 배가 화면에서 떨린다 - 그 떨림이 속도에
        // 비례해 커지는 것이 "빠르면 모션블러" 증상이었다. 하드락이면 배-카메라
        // 상대 속도가 0이라 배는 픽셀에 고정되고 세계가 대신 스크롤한다. 렉으로
        // 한 프레임에 틱이 몰아쳐도 배는 제자리다 - 스파이크는 배경으로만 보인다.
        //
        // 부드러움은 마우스 lookAhead 오프셋에만 남는다.

        // 격파 시퀀스: 마우스를 놓고 배를 비춘다. aim을 0으로 두면 SmoothDamp가
        // 오프셋을 배 중심으로 데려가고(하드락이라 잔해에 그대로 붙는다), 줌은
        // minZoom으로 당겨 침몰을 가까이 본다. 스피드줌도 끈다 - 표류 잔해의
        // 속도로 화면이 넓어지면 죽은 배가 점이 된다.
        if (GameManager.PlayerDown)
            aim = Vector2.zero;

        // 마우스가 중앙에서 멀수록 zoom out
        float mouseZoom =
            Mathf.Lerp(minZoom, maxZoom, aim.magnitude);

        // A가 바뀌는 자리(컷신 프레임 교체 포함)마다 새로 잡는다 - 캐시 하나로 충분하다.
        if (_rigOwner != A)
        {
            _rigOwner = A;
            _targetRig = A.GetComponent<Rigidbody2D>();
        }

        float speed = _targetRig != null ? _targetRig.linearVelocity.magnitude : 0f;
        float speedZoom = Mathf.Min(maxSpeedZoom, speed * moveSmooth * speedZoomFactor);

        // 항해 줌은 접촉이 없을 때만. A가 플레이어 배가 아니면(컷신 프레임) 안 탄다.
        Ship player = A.GetComponent<Ship>();
        bool contact = player == null || !player.IsPlayerControlled || ContactView.HasHostileContact(player);

        if (contact || speed < cruiseExitSpeed)
            _cruising = false;
        else if (speed >= cruiseSpeed)
            _cruising = true;

        if (_cruising)
            speedZoom = cruiseZoom;

        float targetZoom = GameManager.PlayerDown
            ? minZoom
            : Mathf.Max(mouseZoom, speedZoom);

        _aimOffset = Vector2.SmoothDamp(
            _aimOffset,
            aim * lookAhead,
            ref moveVelocity,
            moveSmooth
        );

        Vector2 newPosition = (Vector2)A.position + _aimOffset;

        transform.position = new Vector3(
            newPosition.x,
            newPosition.y,
            transform.position.z
        );

        // 넓어질 때만 느리다. 접촉 순간 돌아오는 쪽은 전투 눈금의 속도 그대로.
        cam.orthographicSize = Mathf.SmoothDamp(
            cam.orthographicSize,
            targetZoom,
            ref zoomVelocity,
            targetZoom > cam.orthographicSize && _cruising ? cruiseZoomSmooth : zoomSmooth
        );

        if (_returnRemaining > 0f)
        {
            _returnRemaining = Mathf.Max(0f, _returnRemaining - Time.unscaledDeltaTime);
            float t = 1f - _returnRemaining / ReturnDuration;
            t = t * t * (3f - 2f * t);
            Vector2 position = Vector2.Lerp((Vector2)A.position + _returnOffset, newPosition, t);
            transform.position = new Vector3(position.x, position.y, transform.position.z);
            cam.orthographicSize = Mathf.Lerp(_returnSize, targetZoom, t);
        }
    }
}
