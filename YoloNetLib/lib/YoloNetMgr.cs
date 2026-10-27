using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

public class YoloNetMgr : MonoBehaviour
{
    public string userName="";
    public enum NetMgrStateEnum
    {
        NotInitialized,
        ConnectingTurn,
        ConnectedTurn,
        ConnectingLobbyMgr,
        ConnectedLobbyMgr,
    };
    public NetMgrStateEnum NetMgrState = NetMgrStateEnum.NotInitialized;
    CancellationTokenSource _cts;
    readonly ConcurrentQueue<(string from, NetworkEntity entity)> _entityQueue = new ConcurrentQueue<(string, NetworkEntity)>();
    readonly ConcurrentQueue<(string from, string text)> _chatQueue = new ConcurrentQueue<(string, string)>();
    readonly Dictionary<string, NESynchro> _remoteEntities = new Dictionary<string, NESynchro>();

    
    void Awake()
    {
        
    }
    void Start()
    {
        Debug.Log("YoloNetMgr Starting");
        ThreadPool.SetMinThreads(32, 32);
        Application.runInBackground  = true;
        Application.targetFrameRate  = 60;

        ProgramClient.OnChatReceived += OnChatReceivedBackground;
        ProgramClient.OnEntityReceived += OnEntityReceivedBackground;

        _cts = new CancellationTokenSource();

        SetUserPseudo();

        NetMgrState = NetMgrStateEnum.ConnectingTurn;
        _ = Task.Run(ProgramClient.AutoClient);
        _ = Task.Run(() => BroadcastLoop(_cts.Token));
    }

    string SetUserPseudo()    {

        //
        // User name
        ProgramClient.myName = userName;
        Debug.Log("Pseudo by unity config : "+ProgramClient.myName);

        // Get App directory
        string appDir = System.IO.Directory.GetParent(Application.dataPath).ToString();
        if (Application.isEditor) appDir = appDir+@"\win64";
        Debug.Log("App dir="+appDir);

        // Config file
        string configFile = appDir+@"\config.cfg";
        IniFile config = new IniFile();
        try {
            config.Load(configFile);
        } catch (Exception)
        {
            Debug.Log("Config file not found: "+configFile);
        }
        string pseudo = config.Get("User", "pseudo");
        if (pseudo!="") ProgramClient.myName = pseudo;
        Debug.Log("Pseudo by config file : "+ProgramClient.myName);

        // Command line 
        string cliname = CommandLineUtils.GetValue("pseudo");
        if (cliname!="") ProgramClient.myName = cliname;
        Debug.Log("Pseudo by command line option --pseudo : "+ProgramClient.myName);
        return ProgramClient.myName;
    }

    void Update()
    {
        while (_entityQueue.TryDequeue(out var item))
            HandleEntity(item.from, item.entity);
        while (_chatQueue.TryDequeue(out var item))
            HandleChat(item.from, item.text);
    }

    void OnDestroy()
    {
        ProgramClient.OnChatReceived -= OnChatReceivedBackground;
        ProgramClient.OnEntityReceived += OnEntityReceivedBackground;
        ProgramClient.Stop();
        _cts?.Cancel();
        _cts?.Dispose();
    }

    // Appelé depuis un thread background → enqueue seulement
    void OnEntityReceivedBackground(string from, NetworkEntity entity) {
        //Debug.Log("OnEntityReceivedBackground Enqueue: "+entity.Serialize());
        _entityQueue.Enqueue((from, entity));
    }
    void OnChatReceivedBackground(string from, string text) {
        //Debug.Log("OnEntityReceivedBackground Enqueue: "+text);
        //_chatQueue.Enqueue((from, text));
        OnRcvChat(from, text);      
    }
    // Appelé depuis Update() → thread Unity, APIs Unity autorisées
    void HandleEntity(string from, NetworkEntity entity)
    {
        /*
        if (entity is NEPlayer.NEPlayerElement p)
        {
            if (p.Name == "") return;

            string key =  p.Name;

            if (!_remoteEntities.TryGetValue(key, out var nep) || nep == null)
            {
                bool isZombie = p.Name.StartsWith("Zombie_");
                var go = Instantiate(isZombie ? prefabZombie : prefabPlayer);
                go.name = key;
                go.transform.SetParent(isZombie ? poolZombies.transform : poolPlayers.transform);
                nep = go.GetComponent<NEPlayer>();
                if (nep == null) nep = go.AddComponent<NEPlayer>();
                nep.isOriginal = false;
                _remoteEntities[key] = nep;
            }

            nep.SetDest(new Vector3(p.X, p.Y, p.Z));            
        } else
        */
        //Debug.Log("HandleEntity "+entity.Serialize());
        if (entity is NetworkEntity ne)
        {
            OnRcvNetworkEntity(from, ne);
        } else
        {
             Debug.Log("HandleEntity not NetworkEntity");
        }
    }

    void HandleChat(string from, string text)
    {
        Debug.Log("HandleChat "+from+" "+text);
        OnRcvChat(from, text);        
    }
    public virtual void OnRcvNetworkEntity(string from, NetworkEntity ne)
    {
        Debug.Log("NetMgr Receive NE ["+ne.Name+"] from "+from+ " ["+ProgramClient.BuildEntityJson(ne).ToString()+"]");       
        
        
    }

    public virtual void OnRcvChat(string from, string text)
    {
        Debug.Log("YoloNetMgr Receive chat from "+from+ " ["+text+"]");        
    }   
    

    Dictionary<string, GameObject> InstancesParents = new Dictionary<string, GameObject>();
    GameObject GetInstancesParent(string instanceType)
    {
        if (InstancesParents.ContainsKey(instanceType))
        {
            return InstancesParents[instanceType];
        }
        GameObject  parent = new GameObject();
        InstancesParents[instanceType] = parent;
        parent.name = instanceType;
        //parent.transform.SetParent(InstancesParent.transform);        
        return parent;
    }

    static async Task BroadcastLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(2000, ct).ConfigureAwait(false); }
            catch (TaskCanceledException) { return; }
            ProgramClient.BroadcastAll("Yo");
        }
    }
}
