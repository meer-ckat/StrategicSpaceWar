using UnityEngine;

[RequireComponent(typeof(Collider2D))]
public class Door : Thing
{
    public bool open = true;

    protected override bool NeedsTick => false;

    public override void OnTick() { }
}
