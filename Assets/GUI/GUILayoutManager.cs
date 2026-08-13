using IMGUI;
using UnityEngine;

public static class GUILayoutManager
{
    // =========================================================
    // Vertical
    // =========================================================

    public static void SetVerticalLayout(
        GUIGroup group,
        float spacing = 10f,
        float padding = 10f)
    {
        SetVerticalLayout(
            group,
            spacing,
            padding,
            padding,
            padding,
            padding
        );
    }


    public static void SetVerticalLayout(
        GUIGroup group,
        float spacing,
        float left,
        float right,
        float top,
        float bottom)
    {
        SetPadding(group, left, right, top, bottom);

        Rect area = GetContentRect(group);

        float y = 0f;
        float maxWidth = 0f;

        foreach (GUIItem item in group.Childrens)
        {
            float width = item.Size.x;

            item.SetRect(new Rect(
                area.x,
                area.y + y,
                width,
                item.Size.y
            ));

            y += item.Size.y + spacing;

            maxWidth = Mathf.Max(
                maxWidth,
                width
            );
        }

        if (group.Childrens.Count > 0)
            y -= spacing;

        group.ContentSize = new Vector2(
            maxWidth,
            y
        );

        GUIGroup.FitGroupToContent(group);
    }


    // =========================================================
    // Horizontal
    // =========================================================

    public static void SetHorizontalLayout(
        GUIGroup group,
        float spacing = 10f,
        float padding = 10f)
    {
        SetHorizontalLayout(
            group,
            spacing,
            padding,
            padding,
            padding,
            padding
        );
    }


    public static void SetHorizontalLayout(
        GUIGroup group,
        float spacing,
        float left,
        float right,
        float top,
        float bottom)
    {
        SetPadding(group, left, right, top, bottom);

        Rect area = GetContentRect(group);

        float x = 0f;
        float maxHeight = 0f;

        foreach (GUIItem item in group.Childrens)
        {
            item.SetRect(new Rect(
                area.x + x,
                area.y,
                item.Size.x,
                item.Size.y
            ));

            x += item.Size.x + spacing;

            maxHeight = Mathf.Max(
                maxHeight,
                item.Size.y
            );
        }

        if (group.Childrens.Count > 0)
            x -= spacing;

        group.ContentSize = new Vector2(
            x,
            maxHeight
        );

        GUIGroup.FitGroupToContent(group);
    }


    // =========================================================
    // Helpers
    // =========================================================

    public static void SetPadding(
        GUIGroup group,
        float padding)
    {
        SetPadding(
            group,
            padding,
            padding,
            padding,
            padding
        );
    }


    public static void SetPadding(
        GUIGroup group,
        float left,
        float right,
        float top,
        float bottom)
    {
        group.PaddingLeft = left;
        group.PaddingRight = right;
        group.PaddingTop = top;
        group.PaddingBottom = bottom;
    }


    public static Rect GetContentRect(
        GUIGroup group)
    {
        return new Rect(
            group.Rect.x + group.PaddingLeft,
            group.Rect.y + group.PaddingTop,

            group.Rect.width
                - group.PaddingLeft
                - group.PaddingRight,

            group.Rect.height
                - group.PaddingTop
                - group.PaddingBottom
        );
    }


    // 기존 API 호환용
    public static Rect GetContentRect(
    GUIGroup group,
    float padding)
    {
    return new Rect(
    group.Rect.x + padding,
    group.Rect.y + padding,

    group.Rect.width - padding * 2f,
    group.Rect.height - padding * 2f
    );
    }

    public static void ClampToScreen(GUIItem item, float margin = 0f)
    {
    ClampInside(
    item,
    new Rect(
        margin,
        margin,
        Screen.width - margin * 2f,
        Screen.height - margin * 2f
    )
    );
    }

    public static void ClampToScreen(
    GUIItem item,
    float left,
    float right,
    float top,
    float bottom)
    {
    ClampInside(
    item,
    new Rect(
        left,
        top,
        Screen.width - left - right,
        Screen.height - top - bottom
    )
    );
    }

    public static void ClampInside(GUIItem item, Rect bounds)
    {
    if (item == null)
    return;

    Vector2 pos = ClampPosition(
    item.Pos,
    item.Size,
    bounds
    );

    // GUIGroup이면 SetPos가 자식까지 같이 옮겨주므로
    // 그룹 전체가 그대로 화면 안으로 이동함.
    item.SetPos(pos);
    }

    public static Vector2 ClampPosition(
    Vector2 position,
    Vector2 size,
    Rect bounds)
    {
    float x = position.x;
    float y = position.y;

    if (size.x <= bounds.width)
    x = Mathf.Clamp(
        x,
        bounds.xMin,
        bounds.xMax - size.x
    );
    else
    x = bounds.xMin;

    if (size.y <= bounds.height)
    y = Mathf.Clamp(
        y,
        bounds.yMin,
        bounds.yMax - size.y
    );
    else
    y = bounds.yMin;

    return new Vector2(x, y);
    }

    public static Rect ClampRect(Rect rect, Rect bounds)
    {
    rect.position = ClampPosition(
    rect.position,
    rect.size,
    bounds
    );

    return rect;
    }

    public static Rect ClampRectToScreen(
    Rect rect,
    float margin = 0f)
    {
    return ClampRect(
    rect,
    new Rect(
        margin,
        margin,
        Screen.width - margin * 2f,
        Screen.height - margin * 2f
    )
    );
    }
    
}