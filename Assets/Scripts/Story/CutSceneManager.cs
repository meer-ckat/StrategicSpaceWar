using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 컷신 동안 화면에 서는 배들. **캠페인의 배가 아니다** - 컷신이 자기 배를 따로 띄우고,
/// 끝나면 전부 치운 뒤 <see cref="Campaign.StartRun"/>으로 진짜 전투를 연다. 프롤로그가
/// 도는 동안 이미 1구역이 굴러가던 것이 그 순서가 없어서였다.
///
/// **컷신은 Campaign을 알고, Campaign은 컷신을 모른다.** 연출이 늘어도 캠페인 쪽 파일은
/// 안 바뀐다 - Battle이 MonoBehaviour가 아닌 것과 같은 방향이다.
///
/// 조종은 ShipAi에 맡기되 뇌를 뗀다(<c>_detatchBrain</c>). 그러면 _targetPos가 적이
/// 아니라 **목적지**로 읽혀서, 배가 그 점 위에 선다. 사격은 Ship의 컷신 두 필드
/// (<c>cutsceneAimAt</c> / <c>cutsceneHoldFire</c>)로 지시한다 - Gun은 컷신을 모른다.
/// </summary>
public class CutSceneManager
{
    public class CutScene_ShipObj
    {
        public readonly string shipDefName;
        public readonly Ship ship;
        public readonly ShipAi ai;

        /// <summary>
        /// 컷신이 만든 배인가. false면 **빌린 배다**(플레이어) - 끝날 때 지우는 것이 아니라
        /// 원래대로 돌려놓아야 한다. 이 한 비트가 두 수명을 가른다.
        /// </summary>
        private readonly bool _owned;

        /// <summary>빌린 배에서 잠시 꺼 둔 입력. 돌려줄 때 다시 켠다.</summary>
        private readonly Behaviour _sleepingInput;

        public GameObject Root => ship != null ? ship.gameObject : null;

        /// <summary>
        /// 이미 씬에 있는 배의 조종간을 컷신이 넘겨받는다. 플레이어 배가 그 경우다.
        ///
        /// **ShipAi를 붙여서 조종한다.** 이동·회전 계산이 거기 다 있고, 컷신 배와 같은
        /// 코드를 타야 두 배가 같은 물리로 움직인다 - 컷신 전용 조종 경로를 따로 만들면
        /// 그 둘은 반드시 어긋난다.
        ///
        /// PlayerInput은 꺼 둔다. 안 끄면 사람이 누르는 값과 컷신 값이 같은 틱에 번갈아
        /// 들어가서 배가 떤다 - 둘 다 Ship.SetPilotInput 하나로 들어가기 때문이다.
        /// </summary>
        internal CutScene_ShipObj(Ship borrowed)
        {
            _owned = false;
            ship = borrowed;
            shipDefName = borrowed != null ? borrowed.shipDefName : null;

            if (borrowed == null)
                return;

            // PlayerInput은 새 InputSystem 어셈블리라 여기서 이름으로 찾는다 - 이 파일이
            // 그 패키지를 직접 참조하지 않아도 되고, 없는 배(적함)에서는 조용히 넘어간다.
            foreach (Behaviour behaviour in borrowed.GetComponents<Behaviour>())
            {
                if (behaviour != null && behaviour.GetType().Name == "PlayerInput" && behaviour.enabled)
                {
                    behaviour.enabled = false;
                    _sleepingInput = behaviour;
                    break;
                }
            }

            ai = borrowed.GetComponent<ShipAi>();

            if (ai == null)
                ai = borrowed.gameObject.AddComponent<ShipAi>();

            ai._detatchBrain = true;
            ai._targetPos = borrowed.transform.position;

            // 손을 뗀 배가 마지막 입력으로 계속 가속하지 않게 조종간을 한 번 비운다.
            borrowed.SetPilotInput(Vector2.zero, 0f);
        }

        internal CutScene_ShipObj(string name, Vector2 spawnPos, float facing, string shipDefName, Ship.Team team)
        {
            _owned = true;
            this.shipDefName = shipDefName;

            // **비활성으로 만들고 마지막에 켠다.** AddComponent는 오브젝트가 활성이면 Awake를
            // 즉시 부르는데, Ship.Awake가 shipDefName을 읽어 배를 통째로 짓는다. 그냥 붙이면
            // 이름이 들어가기 전에 지어져서 빈 배가 태어난다. Campaign.Spawn과 같은 규칙이다.
            var go = new GameObject(name);
            go.SetActive(false);

            go.transform.position = spawnPos;
            go.transform.localScale = new Vector3(facing < 0f ? -1f : 1f, 1f, 1f);

            go.AddComponent<Rigidbody2D>();

            ship = go.AddComponent<Ship>();
            ship.shipDefName = shipDefName;
            ship.team = team;

            ai = go.AddComponent<ShipAi>();

            // 뇌를 떼 둔다. 안 떼면 켜지는 순간 스스로 적을 찾아 움직여서, 연출이 지정한
            // 자리로 가는 대신 교전을 시작한다.
            ai._detatchBrain = true;
            ai._targetPos = (Vector3)spawnPos;

            // 켜기가 실패하면 Thing.Activate가 오브젝트를 지운다 - 그러면 ship·ai가
            // 가짜 null이 되고, 이 클래스는 이미 그 둘을 매번 null 검사하고 쓴다
            // (MoveTo·AimAt·HoldFire...). 연출은 그 배만 빠진 채 이어진다.
            Thing.Activate(go);
        }

        /// <summary>이 배가 갈 자리. ShipAi가 매 틱 그 점을 향해 조종한다.</summary>
        public void MoveTo(Vector2 worldPoint)
        {
            if (ai != null)
                ai._targetPos = (Vector3)worldPoint;
        }

        /// <summary>겨눌 점. null로 되돌리면 포탑이 평소대로 가장 가까운 적을 잡는다.</summary>
        public void AimAt(Vector2? worldPoint)
        {
            if (ship != null)
                ship.cutsceneAimAt = worldPoint;
        }

        /// <summary>켜면 조준은 계속하되 안 쏜다.</summary>
        public void HoldFire(bool hold)
        {
            if (ship != null)
                ship.cutsceneHoldFire = hold;
        }

        /// <summary>방아쇠 없이 쏜다. 수동 주포(플레이어 배)를 컷신이 쏘게 하는 유일한 길이다.</summary>
        public void ForceFire(bool fire)
        {
            if (ship != null)
                ship.cutsceneForceFire = fire;
        }

        /// <summary>
        /// 움직이는 대상을 **리드해서** 쫓는다. null이면 그만 쫓고 마지막 점으로 간다.
        ///
        /// moveTo는 실행 시점 좌표를 고정하므로 회피하는 배를 못 잡는다 - 창이 452 m/s로
        /// 800 m를 날면 그 2초 사이에 표적이 통째로 옆으로 빠진다.
        /// </summary>
        public void Chase(Transform target)
        {
            if (ai != null)
                ai._chase = target;
        }

        /// <summary>
        /// 편대장 옆 정해진 자리를 계속 지킨다. null이면 편대를 푼다.
        ///
        /// <c>moveTo</c>(실행 시점 좌표 고정)도 <c>chase</c>(붙는다)도 아니다 - **간격을
        /// 지키는 것**이라, 편대장이 움직이고 돌아도 상대 위치가 그대로 남는다.
        /// 오프셋은 편대장의 좌표계다.
        /// </summary>
        public void Formation(Transform leader, Vector2 offset)
        {
            if (ai == null)
                return;

            ai._formation = leader;
            ai._formationOffset = offset;
        }

        /// <summary>함체를 이 각도(도)로 돌린다. null이면 다시 진행 방향을 본다.</summary>
        public void Face(float? angle)
        {
            if (ai != null)
                ai._faceAngle = angle;
        }

        public void Boost(bool on)
        {
            if (ship != null)
                ship.cutsceneBoost = on;
        }

        /// <summary>원자로를 터뜨린다. 시뮬레이션의 유폭과 같은 길이라 그림도 같다.</summary>
        public void Detonate()
        {
            if (ship != null)
                ship.DetonateReactor();
        }

        /// <summary>
        /// 컷신이 손을 뗀다. **빌린 배는 원래대로, 만든 배는 지운다.**
        ///
        /// 빌린 배에서 지시를 안 지우면 컷신이 끝난 뒤에도 포탑이 옛 점을 겨누고 부스터가
        /// 켜진 채로 남는다 - 증상이 "왜 배가 이상하게 논다"라 원인이 한참 안 보인다.
        /// </summary>
        internal void ReleaseOrDestroy()
        {
            if (ship != null)
            {
                ship.cutsceneAimAt = null;
                ship.cutsceneHoldFire = false;
                ship.cutsceneForceFire = false;
                ship.cutsceneBoost = false;
            }

            if (_owned)
            {
                if (Root != null)
                    Object.Destroy(Root);

                return;
            }

            // 빌린 배: 붙였던 조종을 걷고 사람에게 돌려준다.
            if (ai != null)
            {
                ai._faceAngle = null;
                ai._chase = null;
                ai._formation = null;
                Object.Destroy(ai);
            }

            if (ship != null)
                ship.SetPilotInput(Vector2.zero, 0f);

            if (_sleepingInput != null)
                _sleepingInput.enabled = true;
        }
    }

    private static readonly Dictionary<string, CutScene_ShipObj> _ships = new();

    public static IReadOnlyDictionary<string, CutScene_ShipObj> Ships => _ships;

    public static bool TryGet(string name, out CutScene_ShipObj ship) => _ships.TryGetValue(name, out ship);

    /// <summary>
    /// 컷신 하나를 연다. **먼저 지난 컷신의 배를 치운다** - 목록이 static이라 씬을 다시
    /// 시작해도 살아남고, 같은 이름을 두 번 넣으면 Add가 예외를 던진다.
    /// </summary>
    public static void Begin()
    {
        Clear();
    }

    public static CutScene_ShipObj SpawnCutSceneShips(
        string cutSceneObjName, Vector2 spawnPos, float facing, string shipDefName, Ship.Team team)
    {
        if (string.IsNullOrEmpty(cutSceneObjName))
        {
            Debug.LogError("[CutScene] 이름 없는 컷신 배는 나중에 못 부른다.");
            return null;
        }

        // **파일부터 본다.** 없으면 Ship.Awake가 판 없는 배를 짓고, 그 배는 화면에
        // 아무것도 안 그리면서 목록에는 들어간다 - 증상이 "안 나온다" 하나뿐이라
        // 연출이 틀린 건지 설계도 이름이 틀린 건지 안 갈린다. Campaign.Spawn과 같은 검사다.
        if (!System.IO.File.Exists(ShipDef.PathOf(shipDefName)))
        {
            Debug.LogError($"[CutScene] '{shipDefName}' 설계도가 없다. '{cutSceneObjName}'을 안 띄운다.");
            return null;
        }

        // 같은 이름을 다시 쓰면 앞의 것을 치우고 자리를 내준다. 예외로 컷신을 끊는 것보다
        // 낫다 - 연출을 고치는 중에 제일 자주 밟는 자리다.
        if (_ships.TryGetValue(cutSceneObjName, out CutScene_ShipObj had))
        {
            Debug.LogWarning($"[CutScene] '{cutSceneObjName}'가 이미 있다. 앞의 것을 치운다.");
            had.ReleaseOrDestroy();
            _ships.Remove(cutSceneObjName);
        }

        var made = new CutScene_ShipObj(cutSceneObjName, spawnPos, facing, shipDefName, team);
        _ships[cutSceneObjName] = made;
        return made;
    }

    /// <summary>
    /// 컷신을 끝내고 진짜 전투를 연다. 배를 먼저 치우는 순서가 중요하다 - 남겨두면
    /// 1구역이 열리는 순간 컷신 배가 그대로 교전에 끼어든다.
    ///
    /// Campaign이 없으면(컷신만 있는 씬) 치우기만 하고 조용히 끝난다.
    /// </summary>
    public static void EndAndStartRun()
    {
        Clear();

        if (Campaign.current != null)
            Campaign.current.StartRun();
    }

    /// <summary>
    /// 이미 씬에 있는 배를 컷신이 넘겨받는다. 플레이어 배가 그 경우다 - 끝나면 지우지
    /// 않고 조종간을 돌려준다.
    ///
    /// 같은 이름으로 두 번 부르면 앞의 것을 그대로 돌려준다. 대본이 여러 줄에서 같은 배를
    /// 가리키는 것이 정상이라, 그때마다 입력을 껐다 켰다 하면 안 된다.
    /// </summary>
    public static CutScene_ShipObj Borrow(string name, Ship ship)
    {
        if (ship == null || string.IsNullOrEmpty(name))
            return null;

        if (_ships.TryGetValue(name, out CutScene_ShipObj had))
            return had;

        var made = new CutScene_ShipObj(ship);
        _ships[name] = made;
        return made;
    }

    /// <summary>배 하나만 컷신에서 뺀다. 만든 배면 지우고, 빌린 배면 돌려준다.</summary>
    public static void Remove(string name)
    {
        if (!_ships.TryGetValue(name, out CutScene_ShipObj ship))
            return;

        ship.ReleaseOrDestroy();
        _ships.Remove(name);
    }

    public static void Clear()
    {
        // **카메라를 먼저 놓는다.** 컷신 배를 지운 뒤에 놓으면 그 한 프레임 동안 카메라가
        // 죽은 Transform을 들고 있고, position을 읽는 자리에서 예외가 난다.
        CameraSystem.ReleaseCutscene();

        foreach (CutScene_ShipObj ship in _ships.Values)
            ship.ReleaseOrDestroy();

        _ships.Clear();
    }
}
