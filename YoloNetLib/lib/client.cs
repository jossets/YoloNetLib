using System;
using System.Collections.Generic;
using System.Globalization;
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

partial class ProgramClient
{
    static ClientWebSocket ws;
    static CancellationTokenSource cts = new CancellationTokenSource();
    // wss via Traefik (443) : le SDP contient le mot de passe ICE et l'empreinte DTLS, il ne doit pas circuler en clair
    public static string ServerUrl = "wss://ysh2.yolospacehacker.com/ws";
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
    static readonly Dictionary<string, RTCDataChannel>    dataChannels = new Dictionary<string, RTCDataChannel>();   // "lobby" : chat, événements (JSON)
    static readonly Dictionary<string, RTCDataChannel>    stateChannels = new Dictionary<string, RTCDataChannel>();  // "state" : transforms (binaire)
    static readonly Dictionary<string, List<string>>      earlyIce     = new Dictionary<string, List<string>>();
    static readonly object peersLock = new object();

    // ------------------------------------------------------------------ //
    // MODE AUTO (appelé depuis Unity)
    // ------------------------------------------------------------------ //

    public static async Task AutoClient()
    {
        _alive = true;
        if (cts.IsCancellationRequested)
            cts = new CancellationTokenSource();

        var requested = myName;
        myName = SanitizeName(myName);
        if (string.IsNullOrEmpty(myName)) myName = GenerateName();
        if (myName != requested && !string.IsNullOrEmpty(requested))
            Log($"[AUTO] Pseudo '{requested}' adapté en '{myName}' (le lobby n'accepte que A-Z a-z 0-9 _ -)");
        Log($"[AUTO] Nom : {myName}");
        await ConnectLoop(auto: true);
    }

    // Mêmes règles que le lobby : [A-Za-z0-9_-]{1,32}. "Sébastien L." → "Sebastien_L_"
    public const int MaxNameLength = 32;
    public const int MaxRoomLength = 48;
    public static string SanitizeName(string name) => SanitizeIdentifier(name, MaxNameLength);
    public static string SanitizeRoomName(string room) => SanitizeIdentifier(room, MaxRoomLength);

    static string SanitizeIdentifier(string name, int maxLength)
    {
        if (string.IsNullOrEmpty(name)) return "";
        var sb = new StringBuilder();
        foreach (var c in name.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue; // accents
            sb.Append(c < 128 && (char.IsLetterOrDigit(c) || c == '_' || c == '-') ? c : '_');
            if (sb.Length == maxLength) break;
        }
        return sb.ToString();
    }

    static string GenerateName()
    {
        var rng     = new System.Random();
        var letters = new string(Enumerable.Range(0, 3).Select(_ => (char)('A' + rng.Next(0, 26))).ToArray());
        var digits  = new string(Enumerable.Range(0, 3).Select(_ => (char)('0' + rng.Next(0, 10))).ToArray());
        return $"{letters}_{digits}";
    }

    // ------------------------------------------------------------------ //
    // BOUCLE DE CONNEXION
    // ------------------------------------------------------------------ //

    static async Task ConnectLoop(bool auto = false)
    {
        bool firstConnect = true;

        while (!cts.IsCancellationRequested)
        {
            CloseAllPeers();
            // Token propre à cette connexion : arrête le heartbeat quand elle tombe
            using var connCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            try
            {
                ws = new ClientWebSocket();
                Log("Connexion...");
                await ws.ConnectAsync(new Uri(ServerUrl), cts.Token);
                Log("Connecté");

                // Reconnexion : rejoindre la room courante (jeton de reconnexion : ni code ni nouvelle validation)
                if (currentRoom != null)
                    await Send(RejoinMessage());

                var receiveTask = ReceiveLoop();
                _ = HeartbeatLoop(connCts.Token);

                if (auto && firstConnect)
                {
                    firstConnect = false;
                    await AutoConnectFlow();
                }

                await receiveTask;

                if (!cts.IsCancellationRequested)
                    Log("Connexion perdue → reconnexion...");
            }
            catch (Exception ex)
            {
                Log("Erreur: " + ex.Message);
            }
            finally
            {
                connCts.Cancel();
            }
            try { await Task.Delay(2000, cts.Token); } catch (OperationCanceledException) { }
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
        var pc = new RTCPeerConnection(TurnIf.GetRTCConfiguration());

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

    public const string StateChannelLabel = "state";

    static void SetupDataChannel(string peerName, RTCDataChannel dc)
    {
        bool isState = dc.label == StateChannelLabel;
        var channels = isState ? stateChannels : dataChannels;
        lock (peersLock) channels[peerName] = dc;
        dc.onopen  += () => Log($"[DC] {peerName}/{dc.label} → canal ouvert");
        dc.onclose += () =>
        {
            Log($"[DC] {peerName}/{dc.label} → canal fermé");
            // Ne retirer que ce canal-ci : une nouvelle connexion au même pair a pu le remplacer
            lock (peersLock)
                if (channels.TryGetValue(peerName, out var current) && current == dc)
                    channels.Remove(peerName);
        };
        if (isState) dc.onmessage += (_, _, data) => OnStateReceived?.Invoke(peerName, data);
        else         dc.onmessage += (_, _, data) => OnMessageRcv(peerName, data);
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
                OnChatReceived?.Invoke(peerName, msg.Text ?? "");
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
    public static event Action<string> OnPeerLeft;   // nom du pair parti (thread réseau)
    public static event Action<string> OnServerError; // message "error" du lobby (thread réseau)
    public static event Action<string, byte[]> OnStateReceived; // paquet binaire du canal "state" (thread réseau, données non fiables : à valider)

    // Envoie un paquet binaire (TransformCodec) à tous les pairs sur le canal "state"
    public static void BroadcastState(byte[] packet)
    {
        List<RTCDataChannel> open;
        lock (peersLock) open = new List<RTCDataChannel>(stateChannels.Values);
        foreach (var dc in open)
            if (dc.readyState == RTCDataChannelState.open)
                dc.send(packet);
    }

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
        // Canal dédié aux transforms. SIPSorcery 10.0.x ignore ordered/maxRetransmits à l'envoi
        // (toujours fiable et ordonné) : on ne gagne pas l'UDP pur, mais un flux SCTP séparé,
        // donc une position retransmise ne bloque pas les événements du canal "lobby".
        var state = await pc.createDataChannel(StateChannelLabel);
        SetupDataChannel(peerName, state);
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
            stateChannels.Clear();
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
                case "peer_joined":
                    Log($"[SYSTÈME] {data.Text}");
                    if (data.Name != null && data.Name != myName) _ = InitiateOffer(data.Name);
                    break;
                case "peer_left":
                    Log($"[SYSTÈME] {data.Text}");
                    if (data.Name != null)
                    {
                        RTCPeerConnection gone;
                        lock (peersLock)
                        {
                            peers.TryGetValue(data.Name, out gone);
                            peers.Remove(data.Name);
                            dataChannels.Remove(data.Name);
                            stateChannels.Remove(data.Name);
                            earlyIce.Remove(data.Name);
                        }
                        try { gone?.close(); } catch { }
                        OnPeerLeft?.Invoke(data.Name);
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
                        LastRttMs = (int)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - data.Timestamp.Value);
                    break;
                default:
                    // room_info, rooms, error, join_*, room_updated, owner_changed : ClientRooms.cs
                    HandleRoomMessage(data);
                    break;
            }
        }
    }

    // Aller-retour client ↔ lobby mesuré par le heartbeat (-1 = pas encore mesuré)
    public static volatile int LastRttMs = -1;

    static async Task HeartbeatLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(5000, ct); }
            catch (OperationCanceledException) { return; }
            if (ws?.State == WebSocketState.Open)
                await Send(new Msg { Type = "ping", Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
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
    [JsonPropertyName("rooms")]      public List<RoomSummary> Rooms { get; set; }
    [JsonPropertyName("ts")]         public long?        Timestamp  { get; set; }
    [JsonPropertyName("candidates")] public List<string> Candidates { get; set; }
    [JsonPropertyName("sdp")]        public string       Sdp        { get; set; }
    [JsonPropertyName("to")]         public string       To         { get; set; }
    [JsonPropertyName("from")]       public string       From       { get; set; }
    [JsonPropertyName("candidate")]  public string       Candidate  { get; set; }
    [JsonPropertyName("owner")]       public string       Owner      { get; set; }
    [JsonPropertyName("entity_type")] public string       EntityType { get; set; }
    [JsonPropertyName("entity_data")] public string       EntityData { get; set; }
    [JsonPropertyName("ice_servers")] public List<IceServerInfo> IceServers { get; set; }
    // Rooms : visibilité ("public"/"private"), places, code d'invitation, jeton de reconnexion, décision de l'owner
    [JsonPropertyName("visibility")]  public string       Visibility { get; set; }
    [JsonPropertyName("max_players")] public int?         MaxPlayers { get; set; }
    [JsonPropertyName("players")]     public int?         Players    { get; set; }
    [JsonPropertyName("code")]        public string       Code       { get; set; }
    [JsonPropertyName("token")]       public string       Token      { get; set; }
    [JsonPropertyName("accept")]      public bool?        Accept     { get; set; }
}
