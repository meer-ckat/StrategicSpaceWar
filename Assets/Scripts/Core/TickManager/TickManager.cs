using System;
using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;

namespace Core
{
    public static class Bootstrapper
    {
        //부트스트래퍼, 씬 로드 직전에 틱 메니저 Gameobject를 생성한다. 만약 틱 메니저가 이미 씬에 있다면 무시한다.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Execute()
        {
            if (TickManager.Instance != null)
                return;

            GameObject coreObject = new GameObject("[Core]");
            UnityEngine.Object.DontDestroyOnLoad(coreObject);

            coreObject.AddComponent<TickManager>();

            Debug.Log("[Bootstrap] Core systems initialized.");
        }
    }

    [DefaultExecutionOrder(-10000)]
    //틱메니저는 이 게임에서 Update() 대신에 사용되는 핵심 클래스이므로 먼저 실행한다.
    public sealed class TickManager : MonoBehaviour
    {
        public static TickManager Instance { get; private set; }

        public const int TickRate = 60; //현재 틱레이트
        public const float TickDeltaTime = 1f / TickRate;

        public static long currentTick { get; private set; }

        // 등록 시점에 ITickLate 여부로 두 리스트에 갈라 넣는다. 하나로 두고 페이즈마다
        // 전체를 돌며 'is ITickLate'를 물으면, 리스너 대부분이 no-op인 판·문·엔진이라
        // 순회 자체가 틱당 2회 전체 완주가 된다. 리스트 안 순서는 등록 순서 그대로라
        // 결정론에 영향 없다 - 두 페이즈는 서로소 집합이다.
        private readonly List<ITick> _early = new(); //힘을 거는 것들
        private readonly List<ITick> _late = new();  //정착된 스냅샷을 읽는 것들(탄)
        //Pending이 있는 이유는, Tick 중에 갑자기 리스너 오브젝트가 사라지면/생기면 리스트를 순회할 때 MissingReferenceException이 나오기 때문에 현재 틱이 끝난 후 제거/생성 대상들을 모아놓는 것이다.
        private readonly List<ITick> _pendingAdd = new(); //현재 Tick 이후 리스너 추가
        private readonly List<ITick> _pendingRemove = new(); //현재 Tick 이후 리스너 제거

        // 목록과 나란히 드는 소속 집합. List.Contains는 O(n)이라 탄환·파편이 틱마다
        // 수십 개 등록되는 판에서 등록 비용이 리스너 수에 비례해 버린다.
        // pending 둘도 같은 이유로 집합을 나란히 든다 - 스폰이 OnTick 안이라(Campaign)
        // 판 629장 등록이 전부 pending을 지나는데, 리스트만 있으면 등록마다 자라는
        // 리스트를 앞에서부터 훑어 스폰 틱 한 번에 비교 수십만 회가 된다.
        // 리스트를 버리지 못하는 이유는 **순서**다: ApplyPendingChanges가 등록 순서대로
        // 목록에 넣어야 틱 순회 순서가 재현된다 - HashSet 순회는 순서가 정의되지 않는다.
        private readonly HashSet<ITick> _listenerSet = new();
        private readonly HashSet<ITick> _pendingAddSet = new();
        private readonly HashSet<ITick> _pendingRemoveSet = new();

        // 마커를 리스너와 나란히 든다. 예전에는 순회마다 MarkerFor(GetType + Dictionary)를
        // 불렀는데, 마커의 Begin/End는 릴리스에서 no-op이어도 **그 조회는 릴리스에도
        // 남는다** - 판 전부가 리스너라 초당 수십만 조회였다. 등록 때 한 번만 찾는다.
        private readonly List<ProfilerMarker> _earlyMarkers = new();
        private readonly List<ProfilerMarker> _lateMarkers = new();

        private List<ITick> ListFor(ITick listener) =>
            listener is ITickLate ? _late : _early;

        private List<ProfilerMarker> MarkersFor(ITick listener) =>
            listener is ITickLate ? _lateMarkers : _earlyMarkers;

        private void AddNow(ITick listener)
        {
            ListFor(listener).Add(listener);
            MarkersFor(listener).Add(MarkerFor(listener));
        }

        private void RemoveNow(ITick listener)
        {
            List<ITick> list = ListFor(listener);
            int index = list.IndexOf(listener);

            if (index < 0)
                return;

            list.RemoveAt(index);
            MarkersFor(listener).RemoveAt(index);
        }

        private bool _isTicking;
        private float _accumulator;

        // 한 프레임에 너무 많은 틱이 몰리는 Spiral of Death 방지
        private const int MaxTicksPerFrame = 8;


        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning(
                    "[TickManager] Duplicate instance detected. Destroying duplicate."
                );

                Destroy(gameObject);
                return;
            }

            Instance = this;
            currentTick = 0;
            _accumulator = 0f;

            // shells start their ray inside the collider they just punched through
            Physics2D.queriesStartInColliders = false;
            Physics2D.simulationMode = SimulationMode2D.Script;
        }


        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }


        public static void Register(ITick listener)
        {
            if (listener == null)
                throw new ArgumentNullException(nameof(listener));

            if (Instance == null)
            {
                Debug.LogError(
                    $"[TickManager] Cannot register {listener}. " +
                    "TickManager has not been initialized."
                );

                return;
            }

            Instance.RegisterInternal(listener);
        }


        public static void Unregister(ITick listener)
        {
            if (listener == null)
                return;

            Instance?.UnregisterInternal(listener);
        }


        internal void RegisterInternal(ITick listener)
        {
            if (_isTicking)
            {
                if (_pendingRemoveSet.Remove(listener))
                    _pendingRemove.Remove(listener);

                if (!_listenerSet.Contains(listener) &&
                    _pendingAddSet.Add(listener))
                {
                    _pendingAdd.Add(listener);
                }

                return;
            }

            if (_listenerSet.Add(listener))
                AddNow(listener);
        }


        internal void UnregisterInternal(ITick listener)
        {
            if (_isTicking)
            {
                if (_pendingAddSet.Remove(listener))
                    _pendingAdd.Remove(listener);

                if (_pendingRemoveSet.Add(listener))
                    _pendingRemove.Add(listener);

                return;
            }

            if (_listenerSet.Remove(listener))
                RemoveNow(listener);
        }


        /// <summary>
        /// true면 시뮬레이션 틱이 완전히 멎는다. 함선 선택 화면이 쓴다 - Time.timeScale과
        /// 따로 있는 이유는 틱 누적이 unscaled deltaTime이 아니어도, 밀린 시간이
        /// 재개 순간 한꺼번에 터지면 안 되기 때문이다. 멎은 동안 누적분은 버린다.
        /// </summary>
        public static bool Paused;

        private void Update()
        {
            if (Paused)
            {
                _accumulator = 0f;
                return;
            }

            _accumulator += Time.deltaTime;

            int ticksProcessed = 0;

            while (_accumulator >= TickDeltaTime &&
                   ticksProcessed < MaxTicksPerFrame)
            {
                RunTick();

                _accumulator -= TickDeltaTime;
                ticksProcessed++;
            }

            // 프레임이 심하게 밀렸을 때 영원히 따라잡으려 하지 않도록 제한
            if (ticksProcessed >= MaxTicksPerFrame &&
                _accumulator >= TickDeltaTime)
            {
                _accumulator %= TickDeltaTime;
            }
        }


        // 프로파일러 마커. 마커 없는 C# 틱 코드는 전부 "TickManager Self"로 뭉개져서
        // 안이 안 보인다 - 리스너 타입별로 갈라 두면 일반 프로파일에 Tick.Ship/Tick.Gun
        // 같은 항목이 바로 나온다. Begin/End는 릴리스 빌드에서 no-op이다.
        private static readonly ProfilerMarker _earlyMarker = new("TickManager.Early");
        private static readonly ProfilerMarker _lateMarker = new("TickManager.Late");
        private static readonly ProfilerMarker _physicsMarker = new("TickManager.Physics2D");
        private static readonly Dictionary<Type, ProfilerMarker> _typeMarkers = new();

        private static ProfilerMarker MarkerFor(ITick listener)
        {
            Type type = listener.GetType();

            if (!_typeMarkers.TryGetValue(type, out ProfilerMarker marker))
                _typeMarkers[type] = marker = new ProfilerMarker("Tick." + type.Name);

            return marker;
        }

        private void RunTick()
        {
            _isTicking = true;

            currentTick++;

            // 리스너 하나가 던져도 _isTicking이 true로 얼어붙으면 등록/해제가 영영 밀린다.
            // 그러면 죽은 리스너가 다음 틱에도 불려서 원래 예외와 상관없는 자리에서
            // 두 번째 예외가 나고, 첫 원인이 그 밑에 묻힌다.
            try
            {
                // 0. 지난 틱 파편 예산에 밀린 것부터. 새 파편이 안 날아오는 틱에도 큐가 마르게
                //    하는 유일한 자리다 - Burst가 부르는 펌프는 새 요청이 있을 때만 돈다.
                SpallResolver.PumpDeferred();

                // 1. 힘을 거는 것들 (함선 추력, 자세)
                using (_earlyMarker.Auto())
                    TickPhase(late: false);

                // 2. 손으로 옮긴 Transform이 있으면 물리에 반영한 뒤,
                //    틱당 정확히 한 번 물리를 돌린다. FixedUpdate가 아니라 여기서 도는 덕에
                //    충돌 해결과 탄 판정이 같은 시계를 쓴다.
                using (_physicsMarker.Auto())
                {
                    Physics2D.SyncTransforms();
                    Physics2D.Simulate(TickDeltaTime);
                }

                // 탄도 스냅샷은 여기서 낡는다. 안 알리면 램 페이즈에 뜬 판 위치를 탄 페이즈가
                // 읽어서, 배가 이동한 만큼 전부 어긋난다.
                TraceWorld.Invalidate();

                // 3. projectiles resolve against that settled snapshot
                using (_lateMarker.Auto())
                    TickPhase(late: true);
            }
            finally
            {
                _isTicking = false;
                ApplyPendingChanges();
            }
        }


        private void TickPhase(bool late)
        {
            // 여기서 IsDestroyed를 안 부른다. unityObject == null은 네이티브 생존 확인이라
            // 리스너 전원 × 2페이즈 × 60틱이면 그것만으로 예산을 먹는데, 잡는 게 거의 없다 -
            // Destroy()는 프레임 끝까지 지연되니 그 사이엔 == null도 false고, 실제 파괴
            // 시점엔 OnDisable → Unregister가 이미 목록에서 뺀다. 남는 구멍은 활성 오브젝트를
            // 런타임에 DestroyImmediate하는 경우뿐이고, 그런 경로는 없다(스폰 직후 재빌드 제외).
            List<ITick> listeners = late ? _late : _early;
            List<ProfilerMarker> markers = late ? _lateMarkers : _earlyMarkers;

            for (int i = 0; i < listeners.Count; i++)
            {
                // **던진 하나가 나머지를 못 죽이게 한다.** 잡지 않으면 예외가 여기서
                // 빠져나가 이 페이즈의 뒤쪽 리스너가 그 틱에 통째로 안 돈다 - 그리고
                // 던진 놈은 대개 자기 Destroy에 못 닿은 것이라(자폭·수명 검사가 그
                // 아래에 있다) 다음 틱에 또 던진다. 증상은 예외 한 줄이 아니라
                // "시뮬레이션 절반이 조용히 멈춤"이고, 원인과 한참 떨어져서 나온다.
                //
                // 그래서 로그만 찍고 넘어가지 않고 **명단에서 뺀다.** 한 번 던진
                // 리스너는 상태가 이미 깨진 것이라 다음 틱에 나을 이유가 없다.
                // Unregister는 순회 중이면 _pendingRemove로 가므로 여기서 안전하다.
                // RunLog.Add가 구독자를 감싸는 것과 같은 태도다.
                try
                {
                    using (markers[i].Auto())
                        listeners[i].OnTick();
                }
                catch (Exception e)
                {
                    Debug.LogError(
                        $"[TickManager] {listeners[i].GetType().Name}이 OnTick에서 던졌다. " +
                        $"명단에서 뺀다: {e}");

                    Unregister(listeners[i]);
                }
            }
        }


        private void ApplyPendingChanges()
        {
            for (int i = 0; i < _pendingRemove.Count; i++)
            {
                if (_listenerSet.Remove(_pendingRemove[i]))
                    RemoveNow(_pendingRemove[i]);
            }

            _pendingRemove.Clear();
            _pendingRemoveSet.Clear();


            for (int i = 0; i < _pendingAdd.Count; i++)
            {
                ITick listener = _pendingAdd[i];

                if (IsDestroyed(listener))
                    continue;

                if (_listenerSet.Add(listener))
                    AddNow(listener);
            }

            _pendingAdd.Clear();
            _pendingAddSet.Clear();
        }


        private static bool IsDestroyed(ITick listener)
        {
            if (listener == null)
                return true;

            if (listener is UnityEngine.Object unityObject)
                return unityObject == null;

            return false;
        }
    }


    public interface ITick
    {
        void OnTick();
    }


    /// <summary>Ticks after Physics2D.SyncTransforms, on the frozen snapshot.</summary>
    public interface ITickLate : ITick { }


    public abstract class TickBehaviour : MonoBehaviour, ITick
    {
        /// <summary>
        /// false = "내 OnTick은 비어 있다"는 선언이고, 등록 자체를 건너뛴다. 판·문·엔진처럼
        /// 사건으로만 사는 것들이 리스너 목록을 수천 개로 불리는 것을 막는다.
        /// **OnTick에 코드를 넣으려면 이 선언부터 지워야 한다** - 남겨두면 조용히 안 돈다.
        /// </summary>
        protected virtual bool NeedsTick => true;

        protected virtual void OnEnable()
        {
            if (NeedsTick)
                TickManager.Register(this);
        }


        protected virtual void OnDisable()
        {
            // 등록 안 된 것을 빼는 것은 무해하다(집합 miss). NeedsTick을 다시 안 보는
            // 이유다 - 파생이 값을 런타임에 바꿔도 여기서 새지 않는다.
            TickManager.Unregister(this);
        }


        public abstract void OnTick();
    }
}