using UnityEngine;

[RequireComponent(typeof(Collider2D))]
public class Door : Thing
{
    public bool open = true;

    public override void OnTick() { }
}
