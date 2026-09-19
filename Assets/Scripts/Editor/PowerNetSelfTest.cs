#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

// Tools > Ship > Run PowerNet Tests. 기대값은 직렬·병렬 합성으로 손 계산한 것이다.
public static class PowerNetSelfTest
{
    private static int _pass, _fail;
    private const float E = 320f;   // 원자로 L-N

    [MenuItem("Tools/Ship/Run PowerNet Tests")]
    public static void Run()
    {
        _pass = 0;
        _fail = 0;

        // 1) 전원(Rs 0.1) → 부하 하나(케이블 0.2, 부하 10)
        {
            var parent = new[] { -1, 0 };
            var rb = new[] { 0.1f, 0.2f };
            var rl = new[] { 0f, 10f };
            var v = new float[2]; var i = new float[2];
            PowerNet.Solve(E, parent, rb, rl, v, i);

            float I = E / (0.1f + 0.2f + 10f);
            Near(i[1], I, "부하 하나: 전류");
            Near(i[0], I, "부하 하나: 전원 전류 = 부하 전류");
            Near(v[1], I * 10f, "부하 하나: 부하 전압");
            Near(v[0], E - I * 0.1f, "부하 하나: 버스 전압");
        }

        // 2) 버스 하나에 부하 둘 병렬 (0.2/10, 0.2/20)
        {
            var parent = new[] { -1, 0, 0 };
            var rb = new[] { 0.1f, 0.2f, 0.2f };
            var rl = new[] { 0f, 10f, 20f };
            var v = new float[3]; var i = new float[3];
            PowerNet.Solve(E, parent, rb, rl, v, i);

            float rPar = 1f / (1f / 10.2f + 1f / 20.2f);
            float I0 = E / (0.1f + rPar);
            float v0 = E - I0 * 0.1f;
            Near(i[0], I0, "병렬: 전원 전류");
            Near(i[1], v0 / 10.2f, "병렬: 부하 1 전류");
            Near(i[2], v0 / 20.2f, "병렬: 부하 2 전류");
            Near(i[1] + i[2], i[0], "병렬: 키르히호프 - 가지 전류 합 = 전원 전류");
        }

        // 3) 사슬: 전원 → 1(부하 10) → 2(케이블 0.3, 부하 10). 노드 1이 부하이자 분기점
        {
            var parent = new[] { -1, 0, 1 };
            var rb = new[] { 0.1f, 0.2f, 0.3f };
            var rl = new[] { 0f, 10f, 10f };
            var v = new float[3]; var i = new float[3];
            PowerNet.Solve(E, parent, rb, rl, v, i);

            float r2 = 0.3f + 10f;
            float r1 = 1f / (1f / 10f + 1f / r2);
            float I0 = E / (0.1f + 0.2f + r1);
            float v1 = E - I0 * 0.3f;
            Near(i[1], I0, "사슬: 1로 들어가는 전류 = 전원 전류");
            Near(v[1], v1, "사슬: 노드 1 전압");
            Near(i[2], v1 / r2, "사슬: 노드 2 전류");
            Near(v[2], v1 - i[2] * 0.3f, "사슬: 노드 2 전압 (한 번 더 떨어진다)");
        }

        // 4) 단락 = 1 mΩ 부하. 특수 케이스가 아니라 그냥 작은 저항이다
        {
            var parent = new[] { -1, 0 };
            var rb = new[] { 0.1f, 0.2f };
            var rl = new[] { 0f, 0.001f };
            var v = new float[2]; var i = new float[2];
            PowerNet.Solve(E, parent, rb, rl, v, i);
            Near(i[1], E / 0.301f, "단락: 전류는 전원+케이블 저항이 정한다");
            Near(v[1], E / 0.301f * 0.001f, "단락: 부하 전압 거의 0");
        }

        // 5) 부하 없음 - 전류 0, 전압은 전부 emf
        {
            var parent = new[] { -1, 0, 1 };
            var rb = new[] { 0.1f, 0.2f, 0.3f };
            var rl = new[] { 0f, 0f, 0f };
            var v = new float[3]; var i = new float[3];
            PowerNet.Solve(E, parent, rb, rl, v, i);
            Near(i[2], 0f, "무부하: 전류 0");
            Near(v[2], E, "무부하: 강하 없음");
        }

        // ---- M3: 모터 = 역기전력을 가진 부하. i = (v - e) / r. 전기기기: E = V - Ia·Ra ----

        // 6) 모터 하나 (Ra 10, e 200). 직렬 한 바퀴: (E - e) / (Rs + 케이블 + Ra)
        {
            var parent = new[] { -1, 0 };
            var rb = new[] { 0.1f, 0.2f };
            var rl = new[] { 0f, 10f };
            var el = new[] { 0f, 200f };
            var v = new float[2]; var i = new float[2];
            PowerNet.Solve(E, parent, rb, rl, el, v, i);

            float I = (E - 200f) / (0.1f + 0.2f + 10f);
            Near(i[1], I, "모터: 전류 = (E - e) / R합");
            Near(v[1], 200f + I * 10f, "모터: 단자 전압 = e + I·Ra");
            Near(v[0], E - I * 0.1f, "모터: 버스 전압");
        }

        // 7) 무부하 회전: e = E 이면 전류 0, 강하 없음 (역기전력이 전원과 맞선다)
        {
            var parent = new[] { -1, 0 };
            var rb = new[] { 0.1f, 0.2f };
            var rl = new[] { 0f, 10f };
            var el = new[] { 0f, E };
            var v = new float[2]; var i = new float[2];
            PowerNet.Solve(E, parent, rb, rl, el, v, i);
            Near(i[1], 0f, "무부하 회전: 전류 0");
            Near(v[1], E, "무부하 회전: 단자 전압 = E");
        }

        // 8) 버스에 저항 부하(0.2/10)와 모터(0.2/10, e 200) 병렬. 버스 전압은 KCL로 손 계산:
        //    (E - v0)/Rs = v0/10.2 + (v0 - 200)/10.2
        {
            var parent = new[] { -1, 0, 0 };
            var rb = new[] { 0.1f, 0.2f, 0.2f };
            var rl = new[] { 0f, 10f, 10f };
            var el = new[] { 0f, 0f, 200f };
            var v = new float[3]; var i = new float[3];
            PowerNet.Solve(E, parent, rb, rl, el, v, i);

            float v0 = (E * 10.2f + 0.1f * 200f) / (10.2f + 0.2f);
            Near(v[0], v0, "병렬 모터: 버스 전압");
            Near(i[1], v0 / 10.2f, "병렬 모터: 저항 부하 전류");
            Near(i[2], (v0 - 200f) / 10.2f, "병렬 모터: 모터 전류");
            Near(i[1] + i[2], i[0], "병렬 모터: 키르히호프");
        }

        // 9) 회생: e = 340 > 버스. 모터가 발전기가 되어 전류가 거꾸로 흐른다 - 부호가 답이다
        {
            var parent = new[] { -1, 0, 0 };
            var rb = new[] { 0.1f, 0.2f, 0.2f };
            var rl = new[] { 0f, 10f, 10f };
            var el = new[] { 0f, 0f, 340f };
            var v = new float[3]; var i = new float[3];
            PowerNet.Solve(E, parent, rb, rl, el, v, i);

            float v0 = (E * 10.2f + 0.1f * 340f) / (10.2f + 0.2f);
            Near(v[0], v0, "회생: 버스 전압이 E에 더 가깝다");
            Near(i[2], (v0 - 340f) / 10.2f, "회생: 모터 전류 음수");
            Near(i[1] + i[2], i[0], "회생: 키르히호프");
        }

        Debug.Log($"[PowerNet] {_pass}개 통과, {_fail}개 실패.");
    }

    private static void Near(float got, float want, string what)
    {
        float tol = Mathf.Max(1e-3f, Mathf.Abs(want) * 1e-3f);   // 0.1 %
        if (Mathf.Abs(got - want) <= tol) { _pass++; return; }
        _fail++;
        Debug.LogError($"[PowerNet] 실패: {what} - 기대 {want:0.###}, 나옴 {got:0.###}");
    }
}
#endif
