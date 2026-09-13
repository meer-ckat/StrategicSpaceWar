#if UNITY_EDITOR
using System.Reflection;
using System.Text;
using UnityEngine;

/// <summary>
/// 순항 측정(에디터 전용). OpenSector 1단계 - 들판 크기를 정하는 숫자는 배가 실제로 얼마나
/// 빨리 가고 얼마나 멀리 미끄러지는가다. 플레이어 배에 붙이면 키 대신 추력을 넣는다:
/// 가속 → 속도가 평평해지면 순항 → 추력 0 → 관성으로 서면 끝. 결과는 로그와 파일 한 줄.
///
/// LateUpdate에 쓰는 이유: Ship.Update가 매 프레임 thrustInput을 0으로 되돌리고 키를 읽는다.
/// 그 뒤에 덮어써야 다음 틱의 힘이 이 값을 쓴다(TickManager는 -10000이라 이번 프레임 틱은 이미 지났다).
/// </summary>
public sealed class CruiseBench : MonoBehaviour
{
    public bool boost;
    public string outPath;

    private enum Phase { Accel, Coast, Done }

    private Ship _ship;
    private FieldInfo _thrust;
    private Phase _phase;
    private float _t0, _tCruise, _cruiseSpeed, _tCoast;
    private Vector2 _p0, _pCruise, _pCoast;
    private float _lastSpeed, _flatFor;
    private readonly StringBuilder _log = new();

    public static CruiseBench Start(Ship ship, bool boost, string outPath)
    {
        var bench = ship.gameObject.AddComponent<CruiseBench>();
        bench.boost = boost;
        bench.outPath = outPath;
        return bench;
    }

    private void Awake()
    {
        _ship = GetComponent<Ship>();
        _thrust = typeof(Ship).GetField("thrustInput", BindingFlags.NonPublic | BindingFlags.Instance);
        _t0 = Time.time;
        _p0 = transform.position;
        _log.AppendLine($"# CruiseBench {_ship.shipDefName} boost={boost} mass={_ship.GetComponent<Rigidbody2D>().mass:0} drag={_ship.drag}");
        _log.AppendLine("t,speed,dist");
    }

    private void LateUpdate()
    {
        if (_ship == null || _phase == Phase.Done)
            return;

        float t = Time.time - _t0;
        float speed = _ship.velocity.magnitude;
        float dist = ((Vector2)transform.position - _p0).magnitude;

        if (Time.frameCount % 15 == 0)
            _log.AppendLine($"{t:0.00},{speed:0.0},{dist:0}");

        switch (_phase)
        {
            case Phase.Accel:
                _thrust.SetValue(_ship, (Vector2)_ship.NoseDirection);
                _ship.pilotBoost = boost;

                // 평평함: 1초 동안 속도 변화가 0.5 m/s 미만이면 순항. 종단속도의 99%쯤이다.
                _flatFor = Mathf.Abs(speed - _lastSpeed) < 0.5f * Time.deltaTime ? _flatFor + Time.deltaTime : 0f;   // 가속도 0.5 m/s² 미만

                if (_flatFor >= 1f && t > 2f)
                {
                    _phase = Phase.Coast;
                    _tCruise = t;
                    _cruiseSpeed = speed;
                    _pCruise = transform.position;
                    _flatFor = 0f;
                }
                break;

            case Phase.Coast:
                _thrust.SetValue(_ship, Vector2.zero);
                _ship.pilotBoost = false;

                if (speed < 1f)
                {
                    _phase = Phase.Done;
                    _tCoast = t;
                    _pCoast = transform.position;
                    Finish();
                }
                break;
        }

        _lastSpeed = speed;
    }

    private void Finish()
    {
        float accelTime = _tCruise;
        float accelDist = (_pCruise - _p0).magnitude;
        float coastTime = _tCoast - _tCruise;
        float coastDist = (_pCoast - _pCruise).magnitude;

        string summary =
            $"[CruiseBench] {_ship.shipDefName} boost={boost}: 0→순항 {accelTime:0.0}s / {accelDist:0} m, " +
            $"순항 {_cruiseSpeed:0.0} m/s, 관성 정지 {coastTime:0.0}s / {coastDist:0} m, " +
            $"60 km 직진 = {60000f / Mathf.Max(1f, _cruiseSpeed) / 60f:0.0}분";

        _log.AppendLine(summary);
        Debug.Log(summary);

        if (!string.IsNullOrEmpty(outPath))
            System.IO.File.AppendAllText(outPath, _log.ToString() + "\n");

        Destroy(this);
    }
}
#endif
