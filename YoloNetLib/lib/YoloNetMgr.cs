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
    readonly ConcurrentQueue<(string from, NetworkEntity entity)> _entityQueue = new ConcurrentQueue<(string, NetworkEntity)>();
    readonly ConcurrentQueue<(string from, string text)> _chatQueue = new ConcurrentQueue<(string, string)>();
    readonly ConcurrentQueue<(string from, TransformState state)> _stateQueue = new ConcurrentQueue<(string, TransformState)>();
    readonly ConcurrentQueue<string> _peerLeftQueue = new ConcurrentQueue<string>();
    readonly ConcurrentQueue<string> _errorQueue = new ConcurrentQueue<string>();
    readonly ConcurrentQueue<Action> _roomEvents = new ConcurrentQueue<Action>();   // événements de room → thread Unity

    // Room rejointe au démarrage (vide : 1re room publique non pleine, sinon création de room_<pseudo>).
    // autoRoom.Options.AutoAccept : en tant que propriétaire, accepter les arrivées sans passer par OnJoinRequest.
    public AutoRoomRequest autoRoom = new AutoRoomRequest();
    readonly Dictionary<string, NESynchro> _remoteEntities = new Dictionary<string, NESynchro>();
    const int MaxQueuedStates = 10000;

    
    void Awake()
    {
        
    }
    // Les sous-classes qui redéfinissent Start/Update/OnDestroy doivent appeler base.Xxx()
    protected virtual void Start()
    {
        Debug.Log("YoloNetMgr Starting");
        ThreadPool.SetMinThreads(32, 32);
        Application.runInBackground  = true;
        Application.targetFrameRate  = 60;

        ProgramClient.OnChatReceived += OnChatReceivedBackground;
        ProgramClient.OnEntityReceived += OnEntityReceivedBackground;
        ProgramClient.OnStateReceived += OnStateReceivedBackground;
        ProgramClient.OnPeerLeft += OnPeerLeftBackground;
        ProgramClient.OnServerError += OnServerErrorBackground;
        ProgramClient.OnRoomJoined           += OnRoomJoinedBackground;
        ProgramClient.OnRoomUpdated          += OnRoomUpdatedBackground;
        ProgramClient.OnJoinPending          += OnJoinPendingBackground;
        ProgramClient.OnJoinRejected         += OnJoinRejectedBackground;
        ProgramClient.OnJoinRequest          += OnJoinRequestBackground;
        ProgramClient.OnJoinRequestCancelled += OnJoinRequestCancelledBackground;

        SetUserPseudo();
        ProgramClient.AutoRoom = autoRoom;

        NetMgrState = NetMgrStateEnum.ConnectingTurn;
        _ = Task.Run(ProgramClient.AutoClient);
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

    protected virtual void Update()
    {
        while (_entityQueue.TryDequeue(out var item))
            HandleEntity(item.from, item.entity);
        while (_chatQueue.TryDequeue(out var item))
            HandleChat(item.from, item.text);
        while (_stateQueue.TryDequeue(out var item))
            OnRcvTransform(item.from, item.state);
        while (_peerLeftQueue.TryDequeue(out var peer))
            OnPeerLeft(peer);
        while (_errorQueue.TryDequeue(out var error))
            OnServerError(error);
        while (_roomEvents.TryDequeue(out var roomEvent))
            roomEvent();
    }

    // Après les Update : envoie en un seul paquet les transforms mis en file pendant la frame
    protected virtual void LateUpdate()
    {
        TransformBroadcaster.Flush();
    }

    protected virtual void OnDestroy()
    {
        ProgramClient.OnChatReceived   -= OnChatReceivedBackground;
        ProgramClient.OnEntityReceived -= OnEntityReceivedBackground;
        ProgramClient.OnStateReceived  -= OnStateReceivedBackground;
        ProgramClient.OnPeerLeft       -= OnPeerLeftBackground;
        ProgramClient.OnServerError    -= OnServerErrorBackground;
        ProgramClient.OnRoomJoined           -= OnRoomJoinedBackground;
        ProgramClient.OnRoomUpdated          -= OnRoomUpdatedBackground;
        ProgramClient.OnJoinPending          -= OnJoinPendingBackground;
        ProgramClient.OnJoinRejected         -= OnJoinRejectedBackground;
        ProgramClient.OnJoinRequest          -= OnJoinRequestBackground;
        ProgramClient.OnJoinRequestCancelled -= OnJoinRequestCancelledBackground;
        ProgramClient.Stop();
    }

    void OnRoomJoinedBackground(RoomState room)           => _roomEvents.Enqueue(() => OnRoomJoined(room));
    void OnRoomUpdatedBackground(RoomState room)          => _roomEvents.Enqueue(() => OnRoomUpdated(room));
    void OnJoinPendingBackground(string room)             => _roomEvents.Enqueue(() => OnJoinPending(room));
    void OnJoinRejectedBackground(string reason)          => _roomEvents.Enqueue(() => OnJoinRejected(reason));
    void OnJoinRequestBackground(string player)           => _roomEvents.Enqueue(() => OnJoinRequest(player));
    void OnJoinRequestCancelledBackground(string player)  => _roomEvents.Enqueue(() => OnJoinRequestCancelled(player));

    // Thread réseau : décodage (sans API Unity) puis enqueue
    readonly List<TransformState> _decodeBuffer = new List<TransformState>();
    void OnStateReceivedBackground(string from, byte[] packet)
    {
        lock (_decodeBuffer)
        {
            _decodeBuffer.Clear();
            if (!TransformCodec.TryDecode(packet, _decodeBuffer)) return;   // paquet mal formé : ignoré
            foreach (var s in _decodeBuffer)
                if (_stateQueue.Count < MaxQueuedStates)
                    _stateQueue.Enqueue((from, s));
        }
    }
    void OnPeerLeftBackground(string peer) => _peerLeftQueue.Enqueue(peer);
    void OnServerErrorBackground(string error) => _errorQueue.Enqueue(error);

    // Appelé depuis un thread background → enqueue seulement
    void OnEntityReceivedBackground(string from, NetworkEntity entity) {
        //Debug.Log("OnEntityReceivedBackground Enqueue: "+entity.Serialize());
        _entityQueue.Enqueue((from, entity));
    }
    // OnRcvChat sera appelé depuis Update() → les sous-classes peuvent utiliser l'API Unity
    void OnChatReceivedBackground(string from, string text) {
        _chatQueue.Enqueue((from, text));
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

    // Transform reçu d'un pair (canal "state", déjà décodé et validé). Thread Unity.
    public virtual void OnRcvTransform(string from, TransformState state) { }

    // Un pair a quitté la room : supprimer ses objets. Thread Unity.
    public virtual void OnPeerLeft(string peer) { }

    // Le lobby a refusé un message (pseudo invalide, room pleine...). Thread Unity.
    public virtual void OnServerError(string error) { }

    // ---- Rooms (thread Unity) ----

    // Entrée dans une room. room.Code : code d'invitation à partager si la room est privée.
    public virtual void OnRoomJoined(RoomState room) { }

    // L'owner a changé les réglages (visibilité, places)
    public virtual void OnRoomUpdated(RoomState room) { }

    // Demande d'accès envoyée, en attente de la décision de l'owner (validation manuelle)
    public virtual void OnJoinPending(string room) { }

    // Accès refusé (par l'owner, room fermée, demande expirée)
    public virtual void OnJoinRejected(string reason) { }

    // Owner avec AutoAccept = false : un joueur demande à entrer.
    // Répondre avec ProgramClient.AnswerJoinRequest(player, true/false), sinon la demande expire au bout de 60 s.
    public virtual void OnJoinRequest(string player)
    {
        Debug.Log($"[ROOM] {player} demande à entrer : surcharger OnJoinRequest pour répondre (AnswerJoinRequest)");
    }

    // La demande de ce joueur a été retirée ou a expiré
    public virtual void OnJoinRequestCancelled(string player) { }
    

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
}
