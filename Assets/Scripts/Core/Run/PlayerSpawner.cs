using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 플레이어 함선을 씬이 아니라 시작할 때 만든다.
///
/// 씬에 놓여 있던 배는 두 가지가 걸렸다: 무엇을 타는지가 **씬과 <see cref="ShipSelectScreen"/>
/// 두 자리**에 있었고(선택이 씬 기본값을 Awake에서 덮어쓰는 방식이라 씬을 열면 늘 V2가 잠깐
/// 태어난다), 카메라가 그 Transform을 직렬화로 붙들고 있어서 배를 지우면 씬 파일이 같이 틀어졌다.
/// 여기서 지으면 배의 출처가 하나다 - 고른 배 아니면 <see cref="defaultShip"/>.
///
/// **비활성으로 만들고 나중에 켠다.** <c>ThingDef.Spawn</c>과 같은 이유다: `Ship.Awake`가
/// `GetComponent&lt;PlayerInput&gt;()`으로 "사람이 모는 배인가"를 정하므로, 활성 오브젝트에
/// AddComponent하면 그 검사가 PlayerInput보다 먼저 돌아 배가 AI 취급으로 태어난다.
/// </summary>
public sealed class PlayerSpawner : MonoBehaviour
{
    [Header("함선")]
    [SerializeField] private string defaultShip = "V2";
    [SerializeField] private Vector2 spawnAt = new(39.3f, -4.4f);
    [SerializeField] private Ship.Team team = Ship.Team.Ally;

    [Header("입력")]
    [SerializeField] private InputActionAsset actions;
    [SerializeField] private string actionMap = "Player";
    [SerializeField] private new Camera camera;

    private void Awake()
    {
        if (actions == null)
        {
            Debug.LogError("[PlayerSpawner] InputActionAsset이 비어 있다. 배를 만들어도 조종이 안 된다.", this);
            return;
        }

        var go = new GameObject("Player");
        go.SetActive(false);
        go.transform.position = spawnAt;

        var body = go.AddComponent<Rigidbody2D>();
        body.gravityScale = 0f;
        body.angularDamping = 0.05f;   // 씬에 있던 값. 나머지 수치는 ShipDef가 붓는다

        Ship ship = go.AddComponent<Ship>();
        ship.team = team;
        ship.shipDefName = string.IsNullOrEmpty(ShipSelectScreen.Chosen) ? defaultShip : ShipSelectScreen.Chosen;

        PlayerInput input = go.AddComponent<PlayerInput>();
        input.actions = actions;

        go.SetActive(true);

        // 액션 맵은 켠 뒤에 고른다 - defaultActionMap은 OnEnable에서 이미 읽힌 값이라 늦다.
        if (!string.IsNullOrEmpty(actionMap) && input.actions.FindActionMap(actionMap, throwIfNotFound: false) != null)
            input.SwitchCurrentActionMap(actionMap);

        if (camera != null)
            input.camera = camera;

        // 카메라는 자기 Start에서 이 배를 붙잡는다 - CameraSystem._instance가 Start에서야
        // 생겨서 여기서는 부를 수 없다.
    }
}
