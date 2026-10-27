using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SIPSorcery.Net;
using UnityEngine;

class ProgramClient
{
    static ClientWebSocket ws;
    static CancellationTokenSource cts = new CancellationTokenSource();
    static string serverUrl = "ws://ysh2.yolospacehacker.com:18080/ws";
    public static string currentRoom;
    public static string myName;
    static string roomOwner;

    static volatile bool _alive = true;
    static void Log(string msg) { if (_alive) Debug.Log(msg); }

    public static void Stop()
    {
        _alive = false;
        cts.Cancel();
        try { ws?.Abort(); } catch { }
        try { dumpWriter?.Dispose(); } catch { }
        dumpWriter = null;
        CloseAllPeers();
    }

    public static bool AmIRoomOwner() =>
        myName != null && myName == roomOwner && currentRoom != null;

    static readonly Dictionary<string, RTCPeerConnection> peers        = new Dictionary<string, RTCPeerConnection>();
    static readonly Dictionary<string, RTCDataChannel>    dataChannels = new Dictionary<string, RTCDataChannel>();
    static readonly Dictionary<string, List<string>>      earlyIce     = new Dictionary<string, List<string>>();
    static readonly object peersLock = new object();

    static volatile TaskCompletionSource<List<string>> pendingRoomsList;

    // ------------------------------------------------------------------ //
    // MODE AUTO (appelé depuis Unity)
    // ------------------------------------------------------------------ //

    public static async Task AutoClient()
    {
        _alive = true;
        if (cts.IsCancellationRequested)
            cts = new CancellationTokenSource();

        
        if (myName=="") myName = GenerateName();
        Log($"[AUTO] Nom généré : {myName}");
        await ConnectLoop(myName, auto: true);
    }

    static string GenerateName()
    {
        var rng     = new System.Random();
        var letters = new string(Enumerable.Range(0, 3).Select(_ => (char)('A' + rng.Next(0, 26))).ToArray());
        var digits  = new string(Enumerable.Range(0, 3).Select(_ => (char)('0' + rng.Next(0, 10))).ToArray());
        return $"{letters}_{digits}";
    }

    // ------------------------------------------------------------------ //
    // AUTO CONNECT FLOW
    // ------------------------------------------------------------------ //

    static async Task AutoConnectFlow(string name)
    {
        Log("[AUTO] Demande de la liste des rooms...");

        pendingRoomsList = new TaskCompletionSource<List<string>>();
        var roomsTask = pendingRoomsList.Task;   // capture avant que ReceiveLoop le null-ise
        await Send(new Msg { Type = "list_rooms" });

        List<string> rooms;
        try
        {
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(5), cts.Token);
            var completed   = await Task.WhenAny(roomsTask, timeoutTask);
            if (completed == timeoutTask) throw new TimeoutException();
            rooms = roomsTask.Result;
        }
        catch (TimeoutException)
        {
            Log("[AUTO] Timeout : pas de réponse du serveur.");
            return;
        }

        if (rooms.Count == 0)
        {
            var roomName = $"room_{name}";
            Log($"[AUTO] Aucune room active → création de '{roomName}'");
            await Send(new Msg { Type = "join", Name = name, Room = roomName });
            currentRoom = roomName;
        }
        else
        {
            var roomName = rooms[0].Split(' ')[0];
            Log($"[AUTO] Room trouvée → rejoins '{roomName}'");
            await Send(new Msg { Type = "join", Name = name, Room = roomName });
            currentRoom = roomName;
            await Task.Delay(300);
            await Send(new Msg { Type = "chat", Name = name, Room = roomName, Text = "bonjour" });
        }
    }

    // ------------------------------------------------------------------ //
    // BOUCLE DE CONNEXION
    // ------------------------------------------------------------------ //

    static async Task ConnectLoop(string name, bool auto = false)
    {
        bool firstConnect = true;

        while (!cts.IsCancellationRequested)
        {
            CloseAllPeers();
            try
            {
                ws = new ClientWebSocket();
                Log("Connexion...");
                await ws.ConnectAsync(new Uri(serverUrl), cts.Token);
                Log("Connecté");

                if (currentRoom != null)
                    await Send(new Msg { Type = "join", Name = name, Room = currentRoom });

                var receiveTask   = ReceiveLoop();
                var heartbeatTask = HeartbeatLoop();

                if (auto && firstConnect)
                {
                    firstConnect = false;
                    await AutoConnectFlow(name);
                }

                await receiveTask;

                if (!cts.IsCancellationRequested)
                    Log("Connexion perdue → reconnexion...");

                await Task.Delay(2000);
            }
            catch (Exception ex)
            {
                Log("Erreur: " + ex.Message);
                await Task.Delay(2000);
            }
        }
    }

    // ------------------------------------------------------------------ //
    // SIGNALING P2P
    // ------------------------------------------------------------------ //

    static string FormatCandidate(string raw)
    {
        var parts = raw.Split(' ');
        if (parts.Length < 8) return raw;
        var label = parts[7] switch { "host" => "HOST", "srflx" => "STUN", "relay" => "TURN", _ => parts[7].ToUpper() };
        string extra = (parts.Length >= 12 && parts[8] == "raddr") ? $"  (reflexive: {parts[9]}:{parts[11]})" : "";
        return $"{label} {parts[2].ToUpper()} {parts[4]}:{parts[5]}{extra}";
    }

    static RTCPeerConnection CreatePeerConnection(string peerName)
    {
        var pc = new RTCPeerConnection(TurnIf.GetRTCConfiguration(myName ?? ""));

        pc.onicecandidate += cand =>
        {
            if (cand == null) return;
            Log($"[ICE] local → {peerName} {FormatCandidate(cand.candidate)}");
            if (currentRoom != null)
                _ = Send(new Msg { Type = "ice_candidate", To = peerName, From = myName, Candidate = cand.candidate });
        };

        pc.onicegatheringstatechange += state => Log($"[ICE] gathering {peerName} → {state}");
        pc.oniceconnectionstatechange += state => Log($"[ICE] {peerName} → {state}");
        pc.onconnectionstatechange += state => Log($"[P2P] {peerName} → {state}");
        pc.ondatachannel += dc => SetupDataChannel(peerName, dc);

        lock (peersLock)
        {
            if (peers.TryGetValue(peerName, out var old))
                try { old.close(); } catch { }
            peers[peerName] = pc;
        }
        return pc;
    }

    static void SetupDataChannel(string peerName, RTCDataChannel dc)
    {
        lock (peersLock) dataChannels[peerName] = dc;
        dc.onopen  += () => Log($"[DC] {peerName} → canal ouvert");
        dc.onclose += () => { Log($"[DC] {peerName} → canal fermé"); lock (peersLock) dataChannels.Remove(peerName); };
        dc.onmessage += (_, _, data) => OnMessageRcv(peerName, data);
    }

    static void OnMessageRcv(string peerName, byte[] data)
    {
        //Log($"Client.OnMessageRcv from {peerName}");
        var text = Encoding.UTF8.GetString(data);
        
        // [DC←YKI_880] {
        //      "type":"entity",
        //      "room":null,
        //      "name":null,
        //      "text":null,
        //      "rooms":null,
        //      "ts":null,
        //      "candidates":null,
        //      "sdp":null,
        //      "to":null,
        //      "from":"YKI_880",
        //      "candidate":null,
        //      "owner":null,
        //      "entity_type":"NEPlayerElement",
        //      "entity_data":"{\u0022X\u0022:-1.84604359,\u0022Y\u0022:0.579960406,\u0022Z\u0022:4.4404707,\u0022NetId\u0022:1,\u0022PrefabType\u0022:\u0022NEPlayerElement\u0022}"}
        //Log($"[DC←{peerName}] {text}");

        // Tente de désérialiser comme NetworkEntity
        try
        {
            //Log($"Client.OnMessageRcv try deserialize");
            var msg = JsonSerializer.Deserialize<Msg>(text);
            if (msg?.Type == "chat") {
                OnChatReceived?.Invoke(peerName, text);
            }
            else if (msg?.Type == "entity" && msg.EntityType != null && msg.EntityData != null)
            {
                //Debug.Log("OnMessageRcv allocate NetworkEntity type "+msg.EntityType);
                //Debug.Log("OnMessageRcv DeSerialize "+msg.EntityData);
                NetworkEntity ne = NetworkEntity.AllocateFromJSON(msg.EntityType, msg.EntityData);                
                //ne.
                //var entity = NetworkEntityRegistry.Deserialize(msg.EntityType, msg.EntityData);
                //if (entity != null) {
                //    Log($"Client.OnMessageRcv Deserialize Ok");
                
                //ne.DeSerialize(msg.EntityData);
                //Debug.Log("Received entity deserialized: "+ne.Serialize());
                OnEntityReceived?.Invoke(peerName, ne);
                //}
                //else 
                //    Log($"Client.OnMessageRcv Deserialize KO");
            } else
            {
                Log($"Client.OnMessageRcv Check KO");
            }
        }
        catch { }
    }
    // ------------------------------------------------------------------ //
    // NETWORK ENTITIES
    // ------------------------------------------------------------------ //

    public static event Action<string, NetworkEntity> OnEntityReceived;
    public static event Action<string, string> OnChatReceived;

    public static bool SendEntityTo(string peerName, NetworkEntity entity)
    {
        RTCDataChannel dc;
        lock (peersLock) dataChannels.TryGetValue(peerName, out dc);
        if (dc?.readyState != RTCDataChannelState.open) return false;
        dc.send(BuildEntityJson(entity));
        return true;
    }

    public static void BroadcastEntity(NetworkEntity entity)
    {
        var json = BuildEntityJson(entity);
        List<RTCDataChannel> open;
        lock (peersLock) open = new List<RTCDataChannel>(dataChannels.Values);
        foreach (var dc in open)
            if (dc.readyState == RTCDataChannelState.open)
                dc.send(json);
    }

    public static string BuildEntityJson(NetworkEntity entity) =>
        JsonSerializer.Serialize(new Msg
        {
            Type       = "entity",
            From       = myName,
            EntityType = entity.GetType().Name,
            EntityData = entity.Serialize(),
        });

    public static bool SendTo(string peerName, string message)
    {
        RTCDataChannel dc;
        lock (peersLock) dataChannels.TryGetValue(peerName, out dc);
        if (dc?.readyState != RTCDataChannelState.open) return false;
        dc.send(message);
        return true;
    }

    public static void BroadcastAll(string message)
    {
        List<RTCDataChannel> open;
        lock (peersLock)
            open = new List<RTCDataChannel>(dataChannels.Values);
        foreach (var dc in open)
            if (dc.readyState == RTCDataChannelState.open)
                dc.send(message);
    }

    static async Task InitiateOffer(string peerName)
    {
        var pc = CreatePeerConnection(peerName);
        var dc = await pc.createDataChannel("lobby");
        SetupDataChannel(peerName, dc);
        var offer = pc.createOffer();
        await pc.setLocalDescription(offer);
        await Send(new Msg { Type = "offer", To = peerName, From = myName, Sdp = offer.sdp });
    }

    static async Task HandleOffer(string from, string sdp)
    {
        var pc = CreatePeerConnection(from);
        pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = sdp });

        List<string> buffered;
        lock (peersLock) { earlyIce.TryGetValue(from, out buffered); earlyIce.Remove(from); }
        if (buffered != null)
            foreach (var c in buffered)
                pc.addIceCandidate(new RTCIceCandidateInit { candidate = c });

        var answer = pc.createAnswer();
        await pc.setLocalDescription(answer);
        await Send(new Msg { Type = "answer", To = from, From = myName, Sdp = answer.sdp });
    }

    static Task HandleAnswer(string from, string sdp)
    {
        RTCPeerConnection pc;
        lock (peersLock) peers.TryGetValue(from, out pc);
        if (pc == null) return Task.CompletedTask;

        pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = sdp });

        List<string> buffered;
        lock (peersLock) { earlyIce.TryGetValue(from, out buffered); earlyIce.Remove(from); }
        if (buffered != null)
            foreach (var c in buffered)
                pc.addIceCandidate(new RTCIceCandidateInit { candidate = c });

        return Task.CompletedTask;
    }

    static Task HandleIceCandidate(string from, string candidate)
    {
        RTCPeerConnection pc;
        lock (peersLock) peers.TryGetValue(from, out pc);

        if (pc != null)
            pc.addIceCandidate(new RTCIceCandidateInit { candidate = candidate });
        else
            lock (peersLock)
            {
                if (!earlyIce.ContainsKey(from)) earlyIce[from] = new List<string>();
                earlyIce[from].Add(candidate);
            }

        return Task.CompletedTask;
    }

    static void CloseAllPeers()
    {
        roomOwner = null;
        lock (peersLock)
        {
            foreach (var pc in peers.Values)
                try { pc.close(); } catch { }
            peers.Clear();
            dataChannels.Clear();
            earlyIce.Clear();
        }
    }

    // ------------------------------------------------------------------ //
    // BOUCLE DE RÉCEPTION
    // ------------------------------------------------------------------ //

    static async Task ReceiveLoop()
    {
        var buffer = new byte[4096];

        while (ws?.State == WebSocketState.Open && !cts.Token.IsCancellationRequested)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult res;
            do
            {
                res = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                if (res.MessageType == WebSocketMessageType.Close) throw new Exception("Serveur fermé");
                ms.Write(buffer, 0, res.Count);
            }
            while (!res.EndOfMessage);

            var json = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
            var data = JsonSerializer.Deserialize<Msg>(json);
            if (data == null) continue;

            if (DumpNetwork) Dump(json);
            switch (data.Type)
            {
                case "chat":
                    // Écho de nos propres messages renvoyé par le serveur → ignoré au plus tôt
                    if (!string.IsNullOrEmpty(myName) &&
                        (data.From == myName || (data.Text != null && data.Text.StartsWith(myName + ":"))))
                        break;
                    OnChatReceived?.Invoke(data.From ?? "", data.Text ?? "");
                    if (LogChat) Log($"[{data.Room}] {data.Text}");
                    break;
                case "system":       Log($"[SYSTÈME] {data.Text}"); break;
                case "room_info":
                    roomOwner = data.Owner;
                    Log(AmIRoomOwner() ? $"[ROOM] {data.Room} — propriétaire" : $"[ROOM] {data.Room} — owner: {data.Owner}");
                    break;
                case "owner_changed":
                    roomOwner = data.Owner;
                    Log(AmIRoomOwner() ? $"[ROOM] Vous êtes le nouveau propriétaire de '{data.Room}'" : $"[ROOM] {data.Text}");
                    break;
                case "peer_joined":
                    Log($"[SYSTÈME] {data.Text}");
                    if (data.Name != null && data.Name != myName) _ = InitiateOffer(data.Name);
                    break;
                case "peer_left":
                    Log($"[SYSTÈME] {data.Text}");
                    if (data.Name != null)
                    {
                        RTCPeerConnection gone;
                        lock (peersLock) { peers.TryGetValue(data.Name, out gone); peers.Remove(data.Name); }
                        try { gone?.close(); } catch { }
                    }
                    break;
                case "offer":
                    if (data.From != null && data.Sdp != null) _ = HandleOffer(data.From, data.Sdp);
                    break;
                case "answer":
                    if (data.From != null && data.Sdp != null) _ = HandleAnswer(data.From, data.Sdp);
                    break;
                case "ice_candidate":
                    if (data.From != null && data.Candidate != null) _ = HandleIceCandidate(data.From, data.Candidate);
                    break;
                case "entity":
                    if (data.EntityType != null && data.EntityData != null)
                    {
                        var entity = NetworkEntityRegistry.Deserialize(data.EntityType, data.EntityData);
                        if (entity != null)
                            OnEntityReceived?.Invoke(data.From ?? "", entity);
                    }
                    break;
                case "pong":
                    if (data.Timestamp.HasValue)
                        Log($"[PING] {DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - data.Timestamp.Value} ms");
                    break;
                case "rooms":
                    var waiter = pendingRoomsList;
                    pendingRoomsList = null;
                    if (waiter != null)
                        waiter.TrySetResult(data.Rooms ?? new List<string>());
                    else if (data.Rooms?.Count > 0)
                        foreach (var r in data.Rooms) Log($"  • {r}");
                    break;
            }
        }
    }

    static async Task HeartbeatLoop()
    {
        while (!cts.Token.IsCancellationRequested)
        {
            await Task.Delay(5000);
            if (ws?.State == WebSocketState.Open)
                await Send(new Msg { Type = "ping" });
        }
    }

    // ClientWebSocket n'autorise qu'un seul SendAsync en cours à la fois
    static readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);

    public static async Task Send(Msg msg)
    {
        if (ws?.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(msg));
        await sendLock.WaitAsync(cts.Token);
        try
        {
            var socket = ws;
            if (socket?.State == WebSocketState.Open)
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException))
        {
            Log("Erreur envoi: " + ex.Message);
        }
        finally
        {
            sendLock.Release();
        }
    }

    // ------------------------------------------------------------------ //
    // DEBUG
    // ------------------------------------------------------------------ //

    // Désactivés par défaut : coûteux à haute fréquence (I/O disque, Debug.Log avec stack trace)
    public static bool DumpNetwork = false;
    public static bool LogChat     = false;

    static StreamWriter dumpWriter;

    static void Dump(string json)
    {
        try
        {
            dumpWriter ??= new StreamWriter(@"net_dump.txt", append: true) { AutoFlush = false };
            dumpWriter.WriteLine(json);
        }
        catch { }
    }
}

class Msg
{
    [JsonPropertyName("type")]       public string       Type       { get; set; }
    [JsonPropertyName("room")]       public string       Room       { get; set; }
    [JsonPropertyName("name")]       public string       Name       { get; set; }
    [JsonPropertyName("text")]       public string       Text       { get; set; }
    [JsonPropertyName("rooms")]      public List<string> Rooms      { get; set; }
    [JsonPropertyName("ts")]         public long?        Timestamp  { get; set; }
    [JsonPropertyName("candidates")] public List<string> Candidates { get; set; }
    [JsonPropertyName("sdp")]        public string       Sdp        { get; set; }
    [JsonPropertyName("to")]         public string       To         { get; set; }
    [JsonPropertyName("from")]       public string       From       { get; set; }
    [JsonPropertyName("candidate")]  public string       Candidate  { get; set; }
    [JsonPropertyName("owner")]       public string       Owner      { get; set; }
    [JsonPropertyName("entity_type")] public string       EntityType { get; set; }
    [JsonPropertyName("entity_data")] public string       EntityData { get; set; }
}
