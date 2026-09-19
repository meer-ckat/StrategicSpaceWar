using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 선분을 사각 두 삼각형으로 쌓는 메시. WireView·SchematicView가 같이 쓴다 - 꼭짓점 색으로 상태를 낸다.
/// Sprites/Default가 꼭짓점 색을 그대로 내므로 재질 하나로 끝난다.
/// </summary>
public sealed class LineMesh
{
    public readonly Mesh mesh = new() { name = "lines" };
    private readonly List<Vector3> _v = new();
    private readonly List<Color> _c = new();
    private readonly List<int> _t = new();

    private static Material _material;
    public static Material Material => _material ??= new Material(Shader.Find("Sprites/Default"));

    public LineMesh() => mesh.MarkDynamic();

    public void Clear()
    {
        _v.Clear(); _c.Clear(); _t.Clear();
    }

    public void Line(Vector2 a, Vector2 b, float width, Color c)
    {
        Vector2 d = b - a;
        if (d.sqrMagnitude < 1e-8f) return;
        Vector2 side = new Vector2(-d.y, d.x).normalized * (width * 0.5f);
        int i = _v.Count;
        _v.Add(a + side); _v.Add(b + side); _v.Add(b - side); _v.Add(a - side);
        _c.Add(c); _c.Add(c); _c.Add(c); _c.Add(c);
        _t.Add(i); _t.Add(i + 1); _t.Add(i + 2); _t.Add(i); _t.Add(i + 2); _t.Add(i + 3);
    }

    public void Polygon(IList<Vector2> pts, float width, Color c)
    {
        for (int i = 0; i < pts.Count; i++)
            Line(pts[i], pts[(i + 1) % pts.Count], width, c);
    }

    public void Rect(Vector2 centre, Vector2 size, float degrees, float width, Color c)
    {
        Vector2 h = size * 0.5f;
        var p = new Vector2[4];
        p[0] = centre + Ballistics.Rotate(new Vector2(-h.x, -h.y), degrees);
        p[1] = centre + Ballistics.Rotate(new Vector2(h.x, -h.y), degrees);
        p[2] = centre + Ballistics.Rotate(new Vector2(h.x, h.y), degrees);
        p[3] = centre + Ballistics.Rotate(new Vector2(-h.x, h.y), degrees);
        Polygon(p, width, c);
    }

    public void Dot(Vector2 at, float size, Color c)
    {
        float h = size * 0.5f;
        int i = _v.Count;
        _v.Add(at + new Vector2(-h, -h)); _v.Add(at + new Vector2(h, -h)); _v.Add(at + new Vector2(h, h)); _v.Add(at + new Vector2(-h, h));
        _c.Add(c); _c.Add(c); _c.Add(c); _c.Add(c);
        _t.Add(i); _t.Add(i + 1); _t.Add(i + 2); _t.Add(i); _t.Add(i + 2); _t.Add(i + 3);
    }

    public void Apply()
    {
        mesh.Clear();
        mesh.SetVertices(_v);
        mesh.SetColors(_c);
        mesh.SetTriangles(_t, 0);
        mesh.RecalculateBounds();
    }

    /// <summary>배 자식으로 MeshFilter+MeshRenderer 하나. 로컬 좌표로 그린다.</summary>
    public static MeshRenderer Attach(Transform parent, string name, Mesh mesh, int sortingOrder)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, worldPositionStays: false);
        go.transform.localPosition = Vector3.zero;
        go.transform.localRotation = Quaternion.identity;
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var r = go.AddComponent<MeshRenderer>();
        r.sharedMaterial = Material;
        r.sortingOrder = sortingOrder;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
        r.enabled = false;
        return r;
    }
}
