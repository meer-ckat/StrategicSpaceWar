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

        private readonly List<ITick> _listeners = new(); //현재 Tick()을 받아야할 대상들
        //Pending이 있는 이유는, Tick 중에 갑자기 리스너 오브젝트가 사라지면/생기면 _listener를 순회할 때 MissingReferenceException이 나오기 때문에 현재 틱이 끝난 후 제거/생성 대상들을 모아놓는 것이다.
        private readonly List<ITick> _pendingAdd = new(); //현재 Tick 이후 리스너 추가
        private readonly List<ITick> _pendingRemove = new(); //현재 Tick 이후 리스너 제거

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

                if (!_listeners.Contains(listener) &&
                    !_pendingAdd.Contains(listener))
                {
                    _pendingAdd.Add(listener);
                }

                return;
            }

            if (!_listeners.Contains(listener))
                _listeners.Add(listener);
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

            _listeners.Remove(listener);
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
            for (int i = 0; i < _listeners.Count; i++)
            {
                ITick listener = _listeners[i];

                if (IsDestroyed(listener))
                {
                    if (!_pendingRemove.Contains(listener))
                        _pendingRemove.Add(listener);

                    continue;
                }

                if ((listener is ITickLate) != late)
                    continue;

                listener.OnTick();
            }
        }


        private void ApplyPendingChanges()
        {
            for (int i = 0; i < _pendingRemove.Count; i++)
            {
                _listeners.Remove(_pendingRemove[i]);
            }

            _pendingRemove.Clear();


            for (int i = 0; i < _pendingAdd.Count; i++)
            {
                ITick listener = _pendingAdd[i];

                if (IsDestroyed(listener))
                    continue;

                if (!_listeners.Contains(listener))
                    _listeners.Add(listener);
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