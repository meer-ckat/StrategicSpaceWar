using UnityEngine;

public class CameraSystem : MonoBehaviour
{
    public enum TrackingType
    {
        Following,
        Centering,
        Aim
    }
    public TrackingType myType;
    public Transform A;
    public Transform B;
    public float ratio;
    Camera cam;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        cam = GetComponent<Camera>();
    }

    // Update is called once per frame
    void Update()
    {
        switch(myType)
        {
            case TrackingType.Centering:
            Centering();
            break;
            case TrackingType.Following:
            Following();
            break;
            case TrackingType.Aim:
            Aim();
            break;
        }
        transform.position = new Vector3(transform.position.x, transform.position.y, -10f);
    }

    void Following()
    {
        transform.position = Vector2.Lerp((Vector2)transform.position, (Vector2)A.position, 0.1f);
    }

    void Centering()
    {
        Vector2 center = (A.position + B.position) / 2;
        cam.orthographicSize = Mathf.Max(10f, (A.position - B.position).magnitude);
        transform.position = Vector2.Lerp((Vector2)transform.position, center, 0.1f);
    }

    void Aim()
    {
        
    }
}
