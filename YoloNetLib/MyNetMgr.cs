using UnityEngine;

public class MyNetMgr : NetMgr
{
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        
    }

    public override void OnRcvNetworkEntity(string from, NetworkEntity ne)
    {
        Debug.Log("MyNetMgr Receive NE : "+ne.Name);
    }
}
