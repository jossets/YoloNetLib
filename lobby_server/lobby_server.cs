using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;

class ProgramServer
{
    record Client(string Name, string Room, WebSocket Socket);

    static readonly ConcurrentDictionary<string, List<Client>> rooms      = [];
    static readonly ConcurrentDictionary<string, string>       roomOwners = []; // room → owner name

    public static async Task MainServer(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var app = builder.Build();

        app.UseWebSockets();

        app.Map("/ws", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }

            var ws = await context.WebSockets.AcceptWebSocketAsync();

            Client? me = null;

            while (ws.State == WebSocketState.Open)
            {
                string json;
                try   { json = await ReceiveFullMessage(ws); }
                catch { break; }

                var data = JsonSerializer.Deserialize<Msg>(json);
                if (data == null) continue;

                switch (data.Type)
                {
                    case "join":
                        // Quitter l'ancienne room si nécessaire
                        if (me != null && rooms.TryGetValue(me.Room, out var oldList))
                        {
                            lock (oldList) oldList.Remove(me);
                            await Broadcast(me.Room, new Msg { Type = "peer_left", Name = me.Name, Room = me.Room, Text = $"{me.Name} a quitté le lobby" });
                            await TransferOwnershipIfNeeded(me.Room, me.Name);
                        }

                        me = new Client(data.Name ?? "?", data.Room ?? "default", ws);
                        rooms.TryAdd(me.Room, []);
                        lock (rooms[me.Room])
                            rooms[me.Room].Add(me);

                        // Premier arrivant = owner
                        if (!roomOwners.ContainsKey(me.Room))
                            roomOwners[me.Room] = me.Name;

                        // Informer le client entrant (owner actuel + sa propre position)
                        await SendToClient(me.Room, me.Name, new Msg
                        {
                            Type  = "room_info",
                            Room  = me.Room,
                            Owner = roomOwners[me.Room],
                            Name  = me.Name
                        });

                        await Broadcast(me.Room, new Msg { Type = "peer_joined", Name = me.Name, Room = me.Room, Text = $"{me.Name} a rejoint le lobby" });
                        break;

                    case "leave":
                        if (me != null && rooms.TryGetValue(me.Room, out var leaveList))
                        {
                            lock (leaveList) leaveList.Remove(me);
                            await Broadcast(me.Room, new Msg { Type = "peer_left", Name = me.Name, Room = me.Room, Text = $"{me.Name} a quitté le lobby" });
                            await TransferOwnershipIfNeeded(me.Room, me.Name);
                            me = null;
                        }
                        break;

                    case "chat":
                        if (me != null)
                            await Broadcast(me.Room, new Msg { Type = "chat", Room = me.Room, Text = $"{me.Name}: {data.Text}" });
                        break;

                    // Signaling WebRTC : routage unicast
                    case "offer":
                    case "answer":
                    case "ice_candidate":
                        if (me != null && data.To != null)
                            await SendToClient(me.Room, data.To, new Msg
                            {
                                Type      = data.Type,
                                From      = me.Name,
                                To        = data.To,
                                Sdp       = data.Sdp,
                                Candidate = data.Candidate
                            });
                        break;

                    case "turn_info":
                        if (me != null)
                            await Broadcast(me.Room, new Msg
                            {
                                Type       = "turn_info",
                                Name       = me.Name,
                                Room       = me.Room,
                                Candidates = data.Candidates,
                                Sdp        = data.Sdp
                            });
                        break;

                    case "list_rooms":
                        var roomList = new List<string>();
                        foreach (var kv in rooms)
                        {
                            int count;
                            lock (kv.Value) count = kv.Value.Count;
                            if (count > 0)
                                roomList.Add($"{kv.Key} ({count} joueur{(count > 1 ? "s" : "")})");
                        }
                        await SendRaw(ws, new Msg { Type = "rooms", Rooms = roomList });
                        break;

                    case "ping":
                        await SendRaw(ws, new Msg { Type = "pong", Timestamp = data.Timestamp });
                        break;
                }
            }

            // Déconnexion : retirer de la room, notifier, transférer ownership si besoin
            if (me != null && rooms.TryGetValue(me.Room, out var list))
            {
                lock (list) list.Remove(me);
                await Broadcast(me.Room, new Msg { Type = "peer_left", Name = me.Name, Room = me.Room, Text = $"{me.Name} a quitté le lobby" });
                await TransferOwnershipIfNeeded(me.Room, me.Name);
            }
        });

        await app.RunAsync("http://0.0.0.0:18080");
    }

    // Si le client qui part était owner, nomme le suivant et informe la room
    static async Task TransferOwnershipIfNeeded(string room, string leavingName)
    {
        if (!roomOwners.TryGetValue(room, out var owner) || owner != leavingName)
            return;

        if (!rooms.TryGetValue(room, out var remaining)) return;

        Client? next;
        lock (remaining) next = remaining.FirstOrDefault(c => c.Socket.State == WebSocketState.Open);

        if (next != null)
        {
            roomOwners[room] = next.Name;
            await Broadcast(room, new Msg
            {
                Type  = "owner_changed",
                Room  = room,
                Owner = next.Name,
                Text  = $"{next.Name} est le nouveau propriétaire du lobby"
            });
        }
        else
        {
            roomOwners.TryRemove(room, out _);
        }
    }

    static async Task<string> ReceiveFullMessage(WebSocket ws)
    {
        var buffer = new byte[4096];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buffer, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new Exception("close");
            ms.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    static async Task Broadcast(string room, Msg msg)
    {
        if (!rooms.TryGetValue(room, out var list)) return;

        var data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(msg));

        List<Client> snapshot;
        lock (list) snapshot = list.ToList();

        foreach (var c in snapshot)
        {
            if (c.Socket.State == WebSocketState.Open)
                try { await c.Socket.SendAsync(data, WebSocketMessageType.Text, true, CancellationToken.None); }
                catch { lock (list) list.Remove(c); }
            else
                lock (list) list.Remove(c);
        }
    }

    static async Task SendToClient(string room, string targetName, Msg msg)
    {
        if (!rooms.TryGetValue(room, out var list)) return;

        Client? target;
        lock (list) target = list.FirstOrDefault(c => c.Name == targetName);

        if (target?.Socket.State == WebSocketState.Open)
            try { await SendRaw(target.Socket, msg); }
            catch { lock (list) list.Remove(target); }
    }

    static async Task SendRaw(WebSocket ws, Msg msg)
    {
        var data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(msg));
        await ws.SendAsync(data, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    class Msg
    {
        [JsonPropertyName("type")]       public string?       Type       { get; set; }
        [JsonPropertyName("room")]       public string?       Room       { get; set; }
        [JsonPropertyName("name")]       public string?       Name       { get; set; }
        [JsonPropertyName("text")]       public string?       Text       { get; set; }
        [JsonPropertyName("rooms")]      public List<string>? Rooms      { get; set; }
        [JsonPropertyName("ts")]         public long?         Timestamp  { get; set; }
        [JsonPropertyName("candidates")] public List<string>? Candidates { get; set; }
        [JsonPropertyName("sdp")]        public string?       Sdp        { get; set; }
        [JsonPropertyName("to")]         public string?       To         { get; set; }
        [JsonPropertyName("from")]       public string?       From       { get; set; }
        [JsonPropertyName("candidate")]  public string?       Candidate  { get; set; }
        [JsonPropertyName("owner")]      public string?       Owner      { get; set; }
    }
}
