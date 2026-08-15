using UnityEngine;

public class StudyProjectile : MonoBehaviour
{
    public float MuzzleSpeed = 1050f;
    public float Mass;//kg
    public float Caliber; //mm. 1 dm = 100mm;
    private Vector2 velocity; //m/s
    public float PenetrationRHA //De marre로 했는데 50.cal 이 미친 1mm RHA가 나와서 미군이 쓴다는걸로 바꿈
    {
        get
        {
            float speed = velocity.magnitude;
            if (speed <= 0 || Caliber <= 0) return 0f;

            // [소총탄 전용 Krupp 변형 모델]
            // 50구경, 5.56mm, 7.62mm 등 소형 화기의 질량(kg)과 구경(mm) 스케일에 완벽히 호환됩니다.
            // 표준 밀리터리 장갑판 저항 상수는 1300 ~ 1500을 사용합니다. (3000은 너무 큽니다)
            float adjustedK = 1400f; 

            // 대폭 개선된 소총탄 스케일 역산 공식
            float insideBracket = (speed * Mathf.Sqrt(Mass)) / (adjustedK * Mathf.Sqrt(Caliber / 100f));
            float rhaDM = Mathf.Pow(insideBracket, 1f / 0.7f);

            // 최종 mm 단위 변환
            return rhaDM * 100f;
        }
    }

    void Update()
    {
        
    }

    void Start()
    {
        Launch(MuzzleSpeed > 20f? MuzzleSpeed : 1000f, Vector2.up);
    }

    public void Launch(float speed, Vector2 dir) //mass와 caliber는 고유 특성이니 무시.
    {
        velocity = speed * dir;
        Debug.Log($"{speed}m/s로 발사!");
        Debug.Log($"{Mass}kg, {Caliber}mm, 따라서 관통력은 {PenetrationRHA}");
    }
}
