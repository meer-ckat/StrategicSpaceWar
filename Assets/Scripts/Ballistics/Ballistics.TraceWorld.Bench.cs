#if UNITY_EDITOR
using System.Collections.Generic;
using System.Text;
using UnityEngine;

/// <summary>
/// 벤치 + 회귀 비교. 운석 격자를 세우고 결정론 레이를 쏜 결과를 파일로 쓴다 - 구현을
/// 바꾸기 전후에 같은 파일을 뽑아 diff하면 판정 보존이 확인된다. 플레이 모드에서
/// execute_code로 부른다. 에디터 전용이라 빌드에 안 들어간다.
/// </summary>
public static partial class TraceWorld
{
    public static string Bench(string outPath, int rocks = 100, int reps = 5)
    {
        var made = new List<GameObject>();
        int side = Mathf.CeilToInt(Mathf.Sqrt(rocks));

        for (int i = 0; i < rocks; i++)
        {
            int gx = i % side, gy = i / side;
            var go = new GameObject($"bench rock {i}");
            go.SetActive(false);
            go.transform.position = new Vector2(600f + gx * 400f, (gy - side * 0.5f) * 400f);
            go.transform.rotation = Quaternion.Euler(0f, 0f, (i * 37) % 360);
            var hulk = go.AddComponent<Hulk>();
            hulk.structureDefName = "asteroid";

            if (Thing.Activate(go))
                made.Add(go);
        }

        var rays = new List<(Vector2 start, Vector2 dir, float range)>();
        var rng = new DeterministicRng(0xBE4C);

        // A. 근접 - 운석 8개 둘레 15~25 m에서 중심을 향해. 대부분 맞는다.
        int[] picks = { 0, 13, 27, 41, 55, 69, 83, 97 };

        foreach (int p in picks)
        {
            if (p >= made.Count) continue;
            Vector2 c = made[p].transform.position;

            for (int k = 0; k < 25; k++)
            {
                float a = rng.Range(0f, Mathf.PI * 2f);
                Vector2 start = c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * rng.Range(15f, 25f);
                Vector2 dir = (c - start).normalized;
                dir = (dir + new Vector2(rng.Range(-0.15f, 0.15f), rng.Range(-0.15f, 0.15f))).normalized;
                rays.Add((start, dir, 40f));
            }
        }

        // B. 안에서 시작 - 품은 판은 안 맞아야 한다(queriesStartInColliders=false).
        for (int k = 0; k < 20; k++)
        {
            Vector2 c = made[picks[k % picks.Length]].transform.position;
            float a = rng.Range(0f, Mathf.PI * 2f);
            rays.Add((c + new Vector2(rng.Range(-1f, 1f), rng.Range(-1f, 1f)), new Vector2(Mathf.Cos(a), Mathf.Sin(a)), 30f));
        }

        // C. 스윕 - 격자를 가로지르는 6 km 레이. 몸 대부분이 후보가 된다.
        for (int k = 0; k < 40; k++)
        {
            float y = (rng.Next01() - 0.5f) * side * 400f;
            rays.Add((new Vector2(-2000f, y), new Vector2(1f, rng.Range(-0.02f, 0.02f)).normalized, 6000f));
        }

        // D. 빈 우주 - 아무것도 안 닿는다.
        for (int k = 0; k < 40; k++)
        {
            float a = rng.Range(0f, Mathf.PI * 2f);
            rays.Add((new Vector2(rng.Range(-9000f, -5000f), rng.Range(-9000f, 9000f)), new Vector2(Mathf.Cos(a), Mathf.Sin(a)), 50f));
        }

        var sb = new StringBuilder();
        var report = new StringBuilder();
        var sw = new System.Diagnostics.Stopwatch();
        double firstMs = 0, totalMs = 0;
        int hits = 0;

        for (int rep = 0; rep < reps; rep++)
        {
            Invalidate();
            sw.Restart();
            bool h0 = Trace(rays[0].start, rays[0].dir, rays[0].range, -1, out Hit hit0);
            double f = sw.Elapsed.TotalMilliseconds;

            if (rep == 0)
                sb.AppendLine(Line(0, h0, in hit0));

            for (int i = 1; i < rays.Count; i++)
            {
                bool h = Trace(rays[i].start, rays[i].dir, rays[i].range, -1, out Hit hit);

                if (rep == 0)
                {
                    sb.AppendLine(Line(i, h, in hit));
                    if (h) hits++;
                }
            }

            double t = sw.Elapsed.TotalMilliseconds;
            firstMs += f;
            totalMs += t;
            report.AppendLine($"rep{rep}: first(build+1ray) {f:F3} ms, all {rays.Count} rays {t:F3} ms, entries {_count}, hulls {_hullCount}{BenchExtra()}");
        }

        report.Insert(0, $"rocks {made.Count}, rays {rays.Count}, hits(rep0) {hits}, avg first {firstMs / reps:F3} ms, avg all {totalMs / reps:F3} ms\n");
        System.IO.File.WriteAllText(outPath, sb.ToString());

        report.Append(BenchDynamic(made, rays));

        foreach (GameObject go in made)
            Object.DestroyImmediate(go);

        return report.ToString();
    }

    private static string Line(int i, bool hit, in Hit h)
    {
        if (!hit)
            return $"{i} miss";

        Collider2D c = ColliderAt(h.index);
        Vector2 p = c.transform.position;
        return $"{i} {c.name} @({p.x:F3},{p.y:F3}) d={h.distance:F4} n=({h.normal.x:F3},{h.normal.y:F3})";
    }

    // 구현별 추가 수치. 기준 측정에서는 없다.
    static partial void BenchExtraHook(StringBuilder sb);

    private static string BenchExtra()
    {
        var sb = new StringBuilder();
        BenchExtraHook(sb);
        return sb.ToString();
    }

    // 캐시 검증(새 구현에서 채운다). 기준 측정에서는 빈 문자열.
    static partial void BenchDynamicHook(List<GameObject> made, List<(Vector2 start, Vector2 dir, float range)> rays, StringBuilder sb);

    private static string BenchDynamic(List<GameObject> made, List<(Vector2 start, Vector2 dir, float range)> rays)
    {
        var sb = new StringBuilder();
        BenchDynamicHook(made, rays, sb);
        return sb.ToString();
    }
}
public static partial class TraceWorld
{
    static partial void BenchExtraHook(StringBuilder sb) =>
        sb.Append($" | bodies {StatBodies} candidates {StatCandidates} posed {StatPosed} posedEntries {StatPosedEntries}");

    /// <summary>캐시 경로와 강제 재굽기(ForceRepose)가 같은 답을 내는지. 움직임·회전·판 제거 뒤.</summary>
    static partial void BenchDynamicHook(List<GameObject> made, List<(Vector2 start, Vector2 dir, float range)> rays, StringBuilder sb)
    {
        string Run()
        {
            var b = new StringBuilder();

            for (int i = 0; i < rays.Count; i++)
            {
                bool h = Trace(rays[i].start, rays[i].dir, rays[i].range, -1, out Hit hit);
                b.Append(i).Append(h ? $" {ColliderAt(hit.index).GetInstanceID()} {hit.distance:R} {hit.normal.x:R} {hit.normal.y:R}" : " miss").Append('\n');
            }

            return b.ToString();
        }

        int Diff(string a, string b)
        {
            string[] la = a.Split('\n'), lb = b.Split('\n');
            int n = 0;
            for (int i = 0; i < Mathf.Max(la.Length, lb.Length); i++)
                if (i >= la.Length || i >= lb.Length || la[i] != lb[i]) n++;
            return n;
        }

        // 1. 틱을 넘어 재사용. 아무것도 안 움직였으니 굽는 몸이 0이어야 하고 답은 같아야 한다.
        Invalidate();
        string cached = Run();
        int posedCached = StatPosed;
        ForceRepose = true;
        Invalidate();
        string forced = Run();
        ForceRepose = false;
        sb.AppendLine($"[reuse, no motion] mismatches {Diff(cached, forced)} | posed cached {posedCached} forced {StatPosed} candidates {StatCandidates}");

        // 2. 회전·이동·판 제거 뒤. 움직인 몸만 다시 굽혀야 한다.
        made[13].transform.Rotate(0f, 0f, 30f);
        made[27].transform.position += new Vector3(3f, -2f, 0f);
        Armor victim = made[41].GetComponentInChildren<Armor>();
        victim.ApplyDamageEvenly(1e9f);
        Invalidate();
        cached = Run();
        posedCached = StatPosed;
        int candidates = StatCandidates;
        ForceRepose = true;
        Invalidate();
        forced = Run();
        ForceRepose = false;
        sb.AppendLine($"[after rotate/move/kill] mismatches {Diff(cached, forced)} | posed cached {posedCached} forced {StatPosed} candidates {candidates} | victim dying {victim.Dying}");

        // 3. 같은 스냅샷 안에서 죽은 판. 굽힌 뒤에 죽어도 채택에서 걸러져야 한다.
        Invalidate();
        Run();
        Armor victim2 = made[55].GetComponentInChildren<Armor>();
        int id2 = victim2.GetComponent<Collider2D>().GetInstanceID();
        victim2.ApplyDamageEvenly(1e9f);
        string after = Run();
        int hitsOnDead = 0;
        foreach (string line in after.Split('\n'))
            if (line.Contains(" " + id2 + " ")) hitsOnDead++;
        sb.AppendLine($"[same-snapshot death] hits on dying collider {hitsOnDead} (expect 0) | dying {victim2.Dying}");
    }
}
#endif
