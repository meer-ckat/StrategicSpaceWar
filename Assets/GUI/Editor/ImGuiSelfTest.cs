#if UNITY_EDITOR
using IMGUI;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Tools > GUI > Run ImGui Tests.
///
/// 여기서 시험하는 것은 **선언 한 번이 이번 프레임의 모양을 전부 정하는가**다. Rect와
/// 텍스트는 매번 대입하면서 Layer·Opacity·RenderScale·isInteractable만 리테인드로 새면,
/// 증상이 "가끔 배경이 대사 위로 올라온다"와 "밑에 있는 버튼이 안 눌린다"로 나온다 - 둘 다
/// 선언한 코드를 아무리 봐도 안 보인다.
///
/// 씬도 플레이 모드도 필요 없다. ImGui의 선언 경로는 GUIItem을 만들고 리스트에 넣을 뿐
/// GUI 함수를 안 부른다 - 그리기는 GUIManager.OnGUI의 일이다.
///
/// **수확(Begin이 지난 프레임 것을 걷는 일)은 여기서 못 잰다.** Begin은 프레임당 한 번만
/// 돌고 메뉴 콜백 하나는 한 프레임이라, 프레임을 넘기는 시험은 플레이 모드가 필요하다.
/// </summary>
public static class ImGuiSelfTest
{
    private static int _pass;
    private static int _fail;

    [MenuItem("Tools/GUI/Run ImGui Tests")]
    public static void Run()
    {
        _pass = 0;
        _fail = 0;

        ImGui.Clear();

        Rect r = new(0f, 0f, 100f, 20f);

        // 1. GUIItem.Opacity의 필드 초기값은 0이고 GUIManager는 그걸 알파에 곱한다.
        //    안 되돌리면 ImGui로 만든 위젯은 전부 "선언은 됐는데 안 보이는 것"이 된다.
        GUILabel label = ImGui.Label("t_label", r, "hi");
        Check("새 라벨은 불투명하다", Mathf.Approximately(label.Opacity, 1f));

        // 2. 장식은 입력 판정에서 빠져야 한다. GetTopMouseLayer가 마우스 아래 interactable
        //    중 최고 Layer를 찾아 그보다 낮은 것을 죽이므로, 화면을 덮는 배경 라벨 하나가
        //    그 밑의 버튼을 통째로 막는다.
        Check("라벨은 입력을 안 받는다", !label.isInteractable);
        Check("버튼은 입력을 받는다", !new GUIButton(GUIContent.none, r).Decorative);

        // 그룹이 그리는 것도 GUI.Box지만 장식이면 안 된다. GUIManager가 그룹 루트에
        // GUI.enabled를 내리면 **자식 버튼까지 같이** 죽는다.
        Check("그룹은 자식을 죽이지 않는다", !new GUIGroup(GUIContent.none, r, "g").Decorative);

        Check("버튼은 첫 선언에 안 눌린 것이다", !ImGui.Button("t_button", r, "ok"));

        // 3. 같은 id는 같은 인스턴스다. 이것이 깨지면 호버·텍스트필드 내용·트윈 진행도가
        //    매 프레임 초기화된다.
        Check("같은 id는 같은 위젯", ReferenceEquals(label, ImGui.Label("t_label", r, "hi")));

        // 4. 여기가 이 파일의 이유다. 지난 프레임에 밀어 넣은 값이 다음 선언에서 살아남으면
        //    안 된다 - 대사가 Layer를 정하는 프레임과 안 정하는 프레임이 갈리는 순간
        //    그리기 순서가 프레임마다 달라진다.
        label.Layer = 77;
        label.Opacity = 0.1f;
        label.RenderScale = Vector2.one * 3f;
        label.isVisible = false;
        label.isInteractable = true;

        GUILabel again = ImGui.Label("t_label", r, "hi");

        Check("Layer가 리셋된다", again.Layer == 0);
        Check("Opacity가 리셋된다", Mathf.Approximately(again.Opacity, 1f));
        Check("RenderScale이 리셋된다", again.RenderScale == Vector2.one);
        Check("isVisible이 리셋된다", again.isVisible);
        Check("장식의 isInteractable이 리셋된다", !again.isInteractable);

        // 5. 종류를 바꿔 다시 선언하면 바꿔 끼운다. 캐스트를 그냥 하면 id 오타 하나에
        //    게임이 죽으므로, 경고만 남기고 새 것으로 간다.
        Debug.Log("[ImGui] 아래 경고 하나는 나오는 것이 정상이다.");
        GUIBoxLabel swapped = ImGui.BoxLabel("t_label", r, "hi");
        Check("종류가 바뀌면 바꿔 끼운다", swapped != null);

        ImGui.Clear();
        Check("Clear가 전부 걷는다", ImGui.LiveCount == 0);

        Debug.Log($"[ImGui] {_pass} pass, {_fail} fail");
    }

    private static void Check(string name, bool ok)
    {
        if (ok)
        {
            _pass++;
            return;
        }

        _fail++;
        Debug.LogError($"[ImGui] FAIL: {name}");
    }
}
#endif
