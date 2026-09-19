using System.Collections;
using System.Collections.Generic;
using Core;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

/// <summary>
/// Space = 정지. 시뮬 틱만 세운다 - 정비 노드와 같은 방식(timeScale은 1). 화면·마우스는 살아 있어서
/// 멈춘 채로 배 속을 읽는다. FTL의 그 정지다. 승무원·전기 관리가 여기서 성립한다.
///
/// 들어가고 나올 때 격납고 건조 전환(<see cref="ArmorSkin.SetBuildFront"/>)을 거꾸로 돈다 - 판이 대각선을
/// 따라 와이어프레임이 되고 배경이 VOID 격자로 바뀐다(<see cref="Blend"/>). 정지 중 Tab은 층을 돈다.
///
/// 안 받는 때: 시스템이 세운 정지(워프·병참·함선 선택 - <see cref="TickManager.Paused"/>), 대본이 Space로
/// 줄을 넘기는 중(<see cref="ScriptManager.Listening"/>), GUI가 꺼진 화면(격파 X-ray - 거기선 Space 3초가 재시작이다).
/// </summary>
public sealed class PauseControl : MonoBehaviour
{
    public enum Layer { All, Power, Air }

    /// <summary>회로도 그림이 켜져 있나 - 정지 중이거나 나가는 전환 중. 렌더러가 본다.</summary>
    public static bool Schematic => TickManager.UserPaused || Blend > 0f;

    /// <summary>정지 중 Tab으로 도는 층. 전부 / 전기(전선·기기) / 기압(방·승무원).</summary>
    public static Layer Focus { get; private set; }

    /// <summary>0 = 게임 화면, 1 = 회로도. 전선·방이 이 값을 곱한다.</summary>
    public static float Blend { get; private set; }

    /// <summary>
    /// 건조 전선의 현재 자리(배 로컬 x+y). 판(PlateSkin)과 배경(Blueprint 셰이더)이 같은 값을 읽어 한 선으로 갈린다.
    /// 평소엔 +∞(전부 게임 화면), 정지가 끝나면 -∞(전부 회로도).
    /// </summary>
    public static float Front { get; private set; } = float.MaxValue;

    public static string FocusLabel => Focus switch { Layer.Power => "전기", Layer.Air => "기압", _ => "전부" };

    private Coroutine _fx;
    private readonly List<ArmorSkin> _skins = new();

    private Vector2 _pressAt;
    private bool _pressing;
    private const float ClickSlop = 4f;   // px. 이 안에서 뗀 것이 클릭, 넘으면 카메라 끌기였다

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install()
    {
        var go = new GameObject("Pause Control");
        DontDestroyOnLoad(go);
        go.AddComponent<PauseControl>();

        // 재시작·씬 전환에 정지가 남으면 안 된다.
        SceneManager.sceneLoaded += (_, _) => { TickManager.UserPaused = false; Blend = 0f; Front = float.MaxValue; Focus = Layer.All; };
    }

    private void Update()
    {
        Keyboard k = Keyboard.current;
        if (k == null) return;

        if (k.spaceKey.wasPressedThisFrame && !TickManager.Paused && !ScriptManager.Listening && !GameManager.GuiHidden)
            Toggle();

        if (TickManager.UserPaused && k.tabKey.wasPressedThisFrame)
            Focus = (Layer)(((int)Focus + 1) % 3);

        ClickToInspect(k);
    }

    // 정지 중 왼쪽 클릭(끌지 않은 것) = 검사. 끌기는 카메라(CameraSystem.PausedCamera)가 먹는다.
    private void ClickToInspect(Keyboard k)
    {
        if (!TickManager.UserPaused || Blend < 1f)
        {
            _pressing = false;
            return;
        }

        if (k.escapeKey.wasPressedThisFrame)
            Inspect.Clear();

        Mouse m = Mouse.current;
        if (m == null) return;

        if (m.leftButton.wasPressedThisFrame)
        {
            _pressing = true;
            _pressAt = m.position.ReadValue();
        }
        else if (m.leftButton.wasReleasedThisFrame && _pressing)
        {
            _pressing = false;
            Vector2 at = m.position.ReadValue();
            Camera cam = Camera.main;

            if ((at - _pressAt).sqrMagnitude <= ClickSlop * ClickSlop && cam != null)
                Inspect.Click(cam.ScreenToWorldPoint(at));
        }
    }

    private void Toggle()
    {
        TickManager.UserPaused = !TickManager.UserPaused;

        if (_fx != null) StopCoroutine(_fx);
        _fx = StartCoroutine(TickManager.UserPaused ? Enter() : Exit());
    }

    // 건조 축(local x+y)의 범위. 배마다 다르지만 한 값으로 쓸어도 된다 - 축이 지나는 순서만 보인다.
    private void CollectSkins(out float min, out float max)
    {
        _skins.Clear();
        min = float.MaxValue; max = float.MinValue;

        for (int i = 0; i < HullStructure.All.Count; i++)
        {
            HullStructure hull = HullStructure.All[i];
            if (hull == null) continue;

            foreach (ArmorSkin skin in hull.GetComponentsInChildren<ArmorSkin>())
            {
                _skins.Add(skin);
                float a = skin.BuildAxis;
                if (a < min) min = a;
                if (a > max) max = a;
            }
        }

        if (_skins.Count == 0) { min = 0f; max = 0f; }
    }

    private void SetFront(float front)
    {
        Front = front;

        for (int i = 0; i < _skins.Count; i++)
            if (_skins[i] != null) _skins[i].SetBuildFront(front);
    }

    // 회로도 윤곽은 건조의 청록 HDR이 아니라 STEEL 0.37(목업에서 고른 값)이다. 들어갈 때 바꾸고 나올 때 되돌린다.
    private void SetWire(Color colour)
    {
        for (int i = 0; i < _skins.Count; i++)
            if (_skins[i] != null) _skins[i].SetWireColor(colour);
    }

    /// <summary>들어간다: 판이 위쪽 대각선부터 와이어프레임이 되고 덮개가 차오른다. 시뮬은 첫 프레임부터 멎어 있다.</summary>
    private IEnumerator Enter()
    {
        CollectSkins(out float min, out float max);
        SetWire(Palette.Steel.WithAlpha(Ballistics.SchematicEdgeAlpha));
        float lo = min - ConstructionFx.Band * 2f, hi = max + ConstructionFx.Band * 2f;

        for (float t = Blend; t < 1f; t += Time.unscaledDeltaTime / Ballistics.SchematicBlendSeconds)
        {
            Blend = t;
            SetFront(Mathf.Lerp(hi, lo, t));
            yield return null;
        }

        Blend = 1f;
        SetFront(float.MinValue);
        _fx = null;
    }

    /// <summary>나온다: 시뮬은 즉시 돈다. 그림만 건조 공개(Reveal)와 같은 방향으로 돌아온다.</summary>
    private IEnumerator Exit()
    {
        CollectSkins(out float min, out float max);
        float lo = min - ConstructionFx.Band * 2f, hi = max + ConstructionFx.Band * 2f;

        for (float t = 1f - Blend; t < 1f; t += Time.unscaledDeltaTime / Ballistics.SchematicBlendSeconds)
        {
            Blend = 1f - t;
            SetFront(Mathf.Lerp(lo, hi, t));
            yield return null;
        }

        Blend = 0f;
        SetFront(float.MaxValue);
        SetWire(ArmorSkin.ConstructionWire);
        Inspect.Clear();   // 선택은 정지의 것이다
        _fx = null;
    }
}
