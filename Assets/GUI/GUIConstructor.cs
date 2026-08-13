using System;
using IMGUI;
using UnityEngine;

public static class Widget
{
    public static GUILabel Label(GUIContent content, Rect rect, GUIStyle style = null)
        => Register(new GUILabel(content, rect, style));

    public static GUILabel Label(string text, Rect rect, GUIStyle style = null)
        => Label(new GUIContent(text), rect, style);

    public static GUILabel Label(GUIGroup parent, GUIContent content, Rect rect, GUIStyle style = null)
        => Add(parent, new GUILabel(content, rect, style));

    public static GUILabel Label(GUIGroup parent, string text, Rect rect, GUIStyle style = null)
        => Label(parent, new GUIContent(text), rect, style);


    public static GUIBoxLabel BoxLabel(GUIContent content, Rect rect, GUIStyle style = null)
        => Register(new GUIBoxLabel(content, rect, style));

    public static GUIBoxLabel BoxLabel(string text, Rect rect, GUIStyle style = null)
        => BoxLabel(new GUIContent(text), rect, style);

    public static GUIBoxLabel BoxLabel(GUIGroup parent, GUIContent content, Rect rect, GUIStyle style = null)
        => Add(parent, new GUIBoxLabel(content, rect, style));

    public static GUIBoxLabel BoxLabel(GUIGroup parent, string text, Rect rect, GUIStyle style = null)
        => BoxLabel(parent, new GUIContent(text), rect, style);


    public static GUIGroup Window(GUIContent content, Rect rect, string name, GUIStyle style = null)
        => Register(new GUIGroup(content, rect, name, style));

    public static GUIGroup Window(string title, Rect rect, string name, GUIStyle style = null)
        => Window(new GUIContent(title), rect, name, style);

    public static GUIGroup Window(GUIGroup parent, GUIContent content, Rect rect, string name, GUIStyle style = null)
        => Add(parent, new GUIGroup(content, rect, name, style));

    public static GUIGroup Window(GUIGroup parent, string title, Rect rect, string name, GUIStyle style = null)
        => Window(parent, new GUIContent(title), rect, name, style);


    public static GUIButton Button(GUIContent content, Rect rect, Action callback = null, GUIStyle style = null)
        => Register(new GUIButton(content, rect, callback, style));

    public static GUIButton Button(string text, Rect rect, Action callback = null, GUIStyle style = null)
        => Button(new GUIContent(text), rect, callback, style);

    public static GUIButton Button(GUIGroup parent, GUIContent content, Rect rect, Action callback = null, GUIStyle style = null)
        => Add(parent, new GUIButton(content, rect, callback, style));

    public static GUIButton Button(GUIGroup parent, string text, Rect rect, Action callback = null, GUIStyle style = null)
        => Button(parent, new GUIContent(text), rect, callback, style);


    public static GUIToggle Toggle(
        GUIContent content,
        Rect rect,
        bool value = false,
        Action<bool> onChanged = null,
        GUIStyle style = null)
        => Register(new GUIToggle(content, rect, value, onChanged, style));

    public static GUIToggle Toggle(
        string text,
        Rect rect,
        bool value = false,
        Action<bool> onChanged = null,
        GUIStyle style = null)
        => Toggle(new GUIContent(text), rect, value, onChanged, style);

    public static GUIToggle Toggle(
        GUIGroup parent,
        GUIContent content,
        Rect rect,
        bool value = false,
        Action<bool> onChanged = null,
        GUIStyle style = null)
        => Add(parent, new GUIToggle(content, rect, value, onChanged, style));

    public static GUIToggle Toggle(
        GUIGroup parent,
        string text,
        Rect rect,
        bool value = false,
        Action<bool> onChanged = null,
        GUIStyle style = null)
        => Toggle(parent, new GUIContent(text), rect, value, onChanged, style);


    public static GUISlider Slider(
        Rect rect,
        float value,
        float min,
        float max,
        Action<float> onChanged = null,
        GUIStyle style = null,
        GUIStyle thumbStyle = null,
        bool vertical = false)
        => Register(new GUISlider(rect, value, min, max, onChanged, style, thumbStyle, vertical));

    public static GUISlider Slider(
        GUIGroup parent,
        Rect rect,
        float value,
        float min,
        float max,
        Action<float> onChanged = null,
        GUIStyle style = null,
        GUIStyle thumbStyle = null,
        bool vertical = false)
        => Add(parent, new GUISlider(rect, value, min, max, onChanged, style, thumbStyle, vertical));


    public static GUITextField TextField(
        Rect rect,
        string value = "",
        Action<string> onChanged = null,
        GUIStyle style = null)
        => Register(new GUITextField(rect, value, onChanged, style));

    public static GUITextField TextField(
        GUIGroup parent,
        Rect rect,
        string value = "",
        Action<string> onChanged = null,
        GUIStyle style = null)
        => Add(parent, new GUITextField(rect, value, onChanged, style));

    public static GUIImage Image(
        Texture texture,
        Rect rect,
        ScaleMode scaleMode = ScaleMode.StretchToFill,
        GUIStyle style = null)
        => Register(new GUIImage(texture, rect, scaleMode, style));

    public static GUIImage Image(
        GUIGroup parent,
        Texture texture,
        Rect rect,
        ScaleMode scaleMode = ScaleMode.StretchToFill,
        GUIStyle style = null)
        => Add(parent, new GUIImage(texture, rect, scaleMode, style));

    public static GUIImage Image(
    Sprite sprite,
    Rect rect,
    ScaleMode scaleMode = ScaleMode.ScaleToFit)
    => Register(new GUIImage(sprite, rect, scaleMode));

    public static GUIImage Image(
    GUIGroup parent,
    Sprite sprite,
    Rect rect,
    ScaleMode scaleMode = ScaleMode.ScaleToFit)
    => Add(parent, new GUIImage(sprite, rect, scaleMode));  


    public static T Layer<T>(T item, int layer) where T : GUIItem
    {
        if (item != null) item.SetLayer(layer);
        return item;
    }

    public static T Visible<T>(T item, bool visible) where T : GUIItem
    {
        if (item != null) item.isVisible = visible;
        return item;
    }

    public static T Enabled<T>(T item, bool enabled) where T : GUIItem
    {
        if (item != null) item.isEnabled = enabled;
        return item;
    }

    public static T Interactable<T>(T item, bool interactable) where T : GUIItem
    {
        if (item != null) item.isInteractable = interactable;
        return item;
    }

    public static T Opacity<T>(T item, float opacity) where T : GUIItem
    {
        if (item != null) item.Opacity = Mathf.Clamp01(opacity);
        return item;
    }


    private static T Register<T>(T item) where T : GUIItem
    {
        if (item == null) return null;

        item.isVisible = true;
        item.isEnabled = true;
        item.isInteractable = true;
        item.Opacity = 1f;

        GUIManager.Register(item);
        return item;
    }

    private static T Add<T>(GUIGroup parent, T item) where T : GUIItem
    {
        if (item == null) return null;

        Register(item);

        if (parent != null)
            parent.Add(item);

        return item;
    }
}