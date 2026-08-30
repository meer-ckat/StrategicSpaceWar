using UnityEngine;
using Core;

public abstract class Thing : TickBehaviour
{
    public int stableId = -1;
    public string defName;
    public long spawnTick;

    /// <summary>
    /// 비활성으로 지어 둔 오브젝트를 켠다. 성공하면 true.
    ///
    /// **이 리포는 오브젝트를 비활성으로 만들고 마지막에 켠다** - AddComponent가 활성
    /// 오브젝트에서 Awake를 즉시 부르기 때문이고, 그래서 값이 다 들어간 뒤에 Awake가
    /// 돌게 하는 것이 규칙이다(ThingDef.Spawn, Campaign.Spawn, CutSceneManager).
    ///
    /// 그 규칙의 뒷면이 여기 있다: **켜는 줄이 던지면 그 아래로 못 내려간다.** 켜기가
    /// 곧 Awake라 Ship.Awake처럼 배를 통째로 짓는 자리가 던지면, 반쯤 지어진 오브젝트가
    /// **비활성인 채 씬에 영원히 남는다** - 컴포넌트가 전부 꺼져 보이고, 틱을 안 받으니
    /// 수명도 자폭도 없고, 목록에 담기는 줄(_spawned.Add)도 대개 켜는 줄 아래라 청소
    /// 대상에도 안 든다. 콘솔의 예외와 화면의 유령이 멀리 떨어져 보여서 둘을 연결하기
    /// 어렵다 - 실제로 미사일에서 그 일이 났다.
    ///
    /// 켜기가 실패한 것은 소환이 실패한 것이다. 즉시 지우고 false를 준다. Destroy가
    /// 아니라 DestroyImmediate인 이유는 Destroy가 프레임 끝까지 미뤄져서, 그 사이에
    /// 격자나 목록을 훑는 코드가 이 시체를 살아 있는 것으로 세기 때문이다.
    /// </summary>
    public static bool Activate(GameObject go)
    {
        if (go == null)
            return false;

        try
        {
            go.SetActive(true);
            return true;
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[Thing] '{go.name}'을 켜는 중 던졌다. 지운다: {e}");
            DestroyImmediate(go);
            return false;
        }
    }

    protected virtual void Awake()
    {
        spawnTick = TickManager.currentTick;
    }

    public float AgeSeconds =>
        (TickManager.currentTick - spawnTick) * TickManager.TickDeltaTime;

    protected virtual void OnDestroy()
    {
        TickManager.Unregister(this);
    }
}