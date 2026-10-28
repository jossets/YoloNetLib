using UnityEngine;

public class MyNetMgr : NetMgr
{
    // Ne pas déclarer de Start()/Update() vides ici : ils masqueraient ceux de NetMgr
    // et le réseau ne démarrerait jamais. Pour en ajouter : override + base.Start()/base.Update().

    public override void OnRcvNetworkEntity(string from, NetworkEntity ne)
    {
        Debug.Log("MyNetMgr Receive NE : "+ne.Name);
        base.OnRcvNetworkEntity(from, ne);
    }
}
