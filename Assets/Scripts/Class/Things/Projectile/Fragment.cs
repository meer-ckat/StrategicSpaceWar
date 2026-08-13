using Core;
using UnityEngine;

public class Fragments : Projectile
{
    public override void OnTick()
    {
        if(TickManager.currentTick % 2 == 0)
            base.OnTick();
    }
}
