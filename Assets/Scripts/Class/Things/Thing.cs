using UnityEngine;
using Core;

public abstract class Thing : TickBehaviour
{
    public string defName;
    public long spawnTick;

    protected virtual void Awake()
    {
        spawnTick = TickManager.currentTick;
    }

    public float AgeSeconds =>
        (TickManager.currentTick - spawnTick) * TickManager.TickDeltaTime;

    protected virtual void OnDestroy()
    {
        TickManager.Unregister(this);
    }
}