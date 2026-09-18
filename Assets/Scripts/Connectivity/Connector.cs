using System.Collections.Generic;
using UnityEngine;

//Connectivity
public class Connector
{
    public static List<Connector> connectors = new(); //전체 커넥터들
    public HashSet<Connector> connectedConnectors = new(); //이웃
    public Vector2 position; //위치
    public float area; //단면적

    public Connector(Vector2 pos, float a)
    {
        position = pos;
        area = a;
        connectors.Add(this);
    }
    public Connector(Vector2 pos, float a, Connector[] connected)
    {
        position = pos;
        area = a;
        connectors.Add(this);
        connectedConnectors.UnionWith(connected);

        foreach (var c in connected)
        {
            c.connectedConnectors.Add(this);
        }
    }
    public Connector(Vector2 pos, float a, int maxConnections)
    {
        position = pos;
        area = a;

        foreach (var c in connectors)
        {
            if(maxConnections > 0 && connectedConnectors.Count >= maxConnections) break;
            if(Mathf.Abs(c.position.x - position.x) > 1f || Mathf.Abs(c.position.y - position.y) > 1f) continue;
            connectedConnectors.Add(c);
            c.connectedConnectors.Add(this);
        }

        connectors.Add(this);
    }

    public HashSet<Connector> Reach()
    {
        Queue<Connector> queue = new();
        HashSet<Connector> visited = new();

        queue.Enqueue(this);
        visited.Add(this);

        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            foreach (var c in cur.connectedConnectors)
            {
                if (visited.Add(c))
                    queue.Enqueue(c);
            }
        }

        return visited;
    }
}
