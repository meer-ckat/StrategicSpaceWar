using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 건조 그림. 두 박자다 - 판이 심어지는 동안은 Telemetry 와이어프레임(설계도가 스스로 그려진다),
/// 완성 순간부터 선체 대각선으로 전선이 지나가며 앞은 실제 장갑, 전선 위 띠가 Radiance.
/// 그림만이다: 시뮬은 Finish에서 이미 통째로 켜졌고, 여기는 PlateSkin의 `_BuildFront` 하나를 민다.
/// 함선(<see cref="Ship"/>)과 <see cref="Hulk"/>가 같은 두 함수를 부른다.
/// </summary>
public static class ConstructionFx
{
    /// <summary>전선이 선체를 지나는 초. 오너 손잡이.</summary>
    public const float RevealSeconds = 1.0f;

    /// <summary>전선 앞뒤로 빛나는 띠(m). 셰이더 `_Build.y`와 같은 값이어야 한다.</summary>
    public const float Band = 1.5f;

    /// <summary>방금 심은 것을 골조로. 판은 와이어프레임, 모듈은 안 보인다(전선이 지날 때 켠다).</summary>
    public static void Wireframe(Thing spawned)
    {
        if (spawned.TryGetComponent(out ArmorSkin skin))
            skin.SetBuildFront(float.MinValue);
        else if (spawned.TryGetComponent(out SolidSkin _) && spawned.TryGetComponent(out SpriteRenderer sprite))
            sprite.enabled = false;
    }

    /// <summary>
    /// 완성 뒤. 판의 선체 좌표 (x+y)를 축으로 전선을 min→max로 민다. 모듈은 전선이 자기 자리를
    /// 지나면 켠다. 끝나면 전부 "완성"(+∞)으로 두어 셰이더의 분기가 죽는다.
    /// </summary>
    public static IEnumerator Reveal(Transform hull)
    {
        var skins = new List<ArmorSkin>(hull.GetComponentsInChildren<ArmorSkin>());
        var modules = new List<(SpriteRenderer sprite, float axis)>();

        foreach (SolidSkin solid in hull.GetComponentsInChildren<SolidSkin>())
        {
            if (solid.TryGetComponent(out SpriteRenderer sprite) && !sprite.enabled)
            {
                Vector2 local = hull.InverseTransformPoint(solid.transform.position);
                modules.Add((sprite, local.x + local.y));
            }
        }

        float min = float.MaxValue, max = float.MinValue;

        foreach (ArmorSkin skin in skins)
        {
            float axis = skin.BuildAxis;
            min = Mathf.Min(min, axis);
            max = Mathf.Max(max, axis);
        }

        if (skins.Count == 0)
            yield break;

        float start = min - Band * 2f;
        float end = max + Band * 2f;

        for (float t = 0f; t < 1f; t += Time.deltaTime / RevealSeconds)
        {
            float front = Mathf.Lerp(start, end, t);

            foreach (ArmorSkin skin in skins)
            {
                if (skin != null)
                    skin.SetBuildFront(front);
            }

            foreach ((SpriteRenderer sprite, float axis) in modules)
            {
                if (sprite != null && axis <= front)
                    sprite.enabled = true;
            }

            yield return null;
        }

        foreach (ArmorSkin skin in skins)
        {
            if (skin != null)
                skin.SetBuildFront(float.MaxValue);
        }

        foreach ((SpriteRenderer sprite, _) in modules)
        {
            if (sprite != null)
                sprite.enabled = true;
        }
    }
}
