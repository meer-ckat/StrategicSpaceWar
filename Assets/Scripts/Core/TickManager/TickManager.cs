using System;
using System.Collections.Generic;
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
        private readonly HashSet<ITick> _listenerSet = new();

        private List<ITick> ListFor(ITick listener) =>
            listener is ITickLate ? _late : _early;

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
                _pendingRemove.Remove(listener);

                if (!_listenerSet.Contains(listener) &&
                    !_pendingAdd.Contains(listener))
                {
                    _pendingAdd.Add(listener);
                }

                return;
            }

            if (_listenerSet.Add(listener))
                ListFor(listener).Add(listener);
        }


        internal void UnregisterInternal(ITick listener)
        {
            if (_isTicking)
            {
                _pendingAdd.Remove(listener);

                if (!_pendingRemove.Contains(listener))
                    _pendingRemove.Add(listener);

                return;
            }

            if (_listenerSet.Remove(listener))
                ListFor(listener).Remove(listener);
        }


        private void Update()
        {
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


        private void RunTick()
        {
            _isTicking = true;

            currentTick++;

            // 1. 힘을 거는 것들 (함선 추력, 자세)
            TickPhase(late: false);

            // 2. 손으로 옮긴 Transform이 있으면 물리에 반영한 뒤,
            //    틱당 정확히 한 번 물리를 돌린다. FixedUpdate가 아니라 여기서 도는 덕에
            //    충돌 해결과 탄 판정이 같은 시계를 쓴다.
            Physics2D.SyncTransforms();
            Physics2D.Simulate(TickDeltaTime);

            // 3. projectiles resolve against that settled snapshot
            TickPhase(late: true);

            _isTicking = false;

            ApplyPendingChanges();
        }


        private void TickPhase(bool late)
        {
            // 여기서 IsDestroyed를 안 부른다. unityObject == null은 네이티브 생존 확인이라
            // 리스너 전원 × 2페이즈 × 60틱이면 그것만으로 예산을 먹는데, 잡는 게 거의 없다 -
            // Destroy()는 프레임 끝까지 지연되니 그 사이엔 == null도 false고, 실제 파괴
            // 시점엔 OnDisable → Unregister가 이미 목록에서 뺀다. 남는 구멍은 활성 오브젝트를
            // 런타임에 DestroyImmediate하는 경우뿐이고, 그런 경로는 없다(스폰 직후 재빌드 제외).
            List<ITick> listeners = late ? _late : _early;

            for (int i = 0; i < listeners.Count; i++)
                listeners[i].OnTick();
        }


        private void ApplyPendingChanges()
        {
            for (int i = 0; i < _pendingRemove.Count; i++)
            {
                if (_listenerSet.Remove(_pendingRemove[i]))
                    ListFor(_pendingRemove[i]).Remove(_pendingRemove[i]);
            }

            _pendingRemove.Clear();


            for (int i = 0; i < _pendingAdd.Count; i++)
            {
                ITick listener = _pendingAdd[i];

                if (IsDestroyed(listener))
                    continue;

                if (_listenerSet.Add(listener))
                    ListFor(listener).Add(listener);
            }

            _pendingAdd.Clear();
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
        protected virtual void OnEnable()
        {
            TickManager.Register(this);
        }


        protected virtual void OnDisable()
        {
            TickManager.Unregister(this);
        }


        public abstract void OnTick();
    }
}