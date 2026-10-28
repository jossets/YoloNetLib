using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.AspNetCore.HttpOverrides;

class ProgramServer
{
    // ------------------------------------------------------------------ //
    // LIMITES (anti-abus)
    // ------------------------------------------------------------------ //

    const int    MaxMessageBytes     = 16 * 1024;
    const int    MaxConnections      = 500;
    const int    MaxConnectionsPerIp = 16;    // plusieurs joueurs derrière une même box restent possibles
    const double RatePerSecond       = 40;    // messages/s soutenus par connexion
    const double RateBurst           = 80;    // rafale tolérée (échange ICE au join)
    const int    MaxDroppedMessages  = 200;   // au-delà : déconnexion
    static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);   // le client ping toutes les 5 s

    // Origines navigateur autorisées (header Origin). Le client Unity n'envoie pas d'Origin :
    // toute requête qui en porte un vient d'une page web et est refusée sauf si listée ici.
    static readonly HashSet<string> AllowedOrigins = [];

    // Une WebSocket n'accepte qu'un seul SendAsync à la fois : un verrou par connexion
    static readonly ConcurrentDictionary<WebSocket, SemaphoreSlim> sendLocks = [];

    static int connectionCount;
    static readonly ConcurrentDictionary<IPAddress, int> connectionsPerIp = [];

    static ILogger log = null!;

    public static async Task MainServer(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.ConfigureKestrel(o =>
        {
            o.AddServerHeader = false;
            o.Limits.MaxRequestHeadersTotalSize = 8 * 1024;
            o.Limits.MaxRequestBodySize         = 0;
        });
        var app = builder.Build();
        log = app.Logger;
        TurnCredentials.Configure(log);

        // Derrière Traefik : l'IP réelle du client est dans X-Forwarded-For.
        // On ne lit ce header que s'il vient du réseau Docker du proxy, sinon un client pourrait choisir son IP.
        var trustedProxyNetwork = Environment.GetEnvironmentVariable("LOBBY_TRUSTED_PROXY_NETWORK");
        if (!string.IsNullOrEmpty(trustedProxyNetwork))
        {
            var forwarded = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor };
            forwarded.KnownProxies.Clear();
            forwarded.KnownIPNetworks.Clear();
            forwarded.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(trustedProxyNetwork));
            app.UseForwardedHeaders(forwarded);
        }

        app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(15),
            KeepAliveTimeout  = TimeSpan.FromSeconds(15),
        });

        app.Map("/ws", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }

            var origin = context.Request.Headers.Origin.ToString();
            if (origin.Length > 0 && !AllowedOrigins.Contains(origin))
            {
                context.Response.StatusCode = 403;
                return;
            }

            var ip = context.Connection.RemoteIpAddress ?? IPAddress.None;
            if (!TryAcquireConnectionSlot(ip))
            {
                log.LogWarning("Connexion refusée (limite atteinte) depuis {Ip}", ip);
                context.Response.StatusCode = 429;
                return;
            }

            try
            {
                var ws = await context.WebSockets.AcceptWebSocketAsync();
                sendLocks[ws] = new SemaphoreSlim(1, 1);
                try     { await HandleConnection(ws, ip); }
                finally
                {
                    sendLocks.TryRemove(ws, out _);
                    ws.Abort();
                }
            }
            finally
            {
                ReleaseConnectionSlot(ip);
            }
        });

        _ = Task.Run(ExpirePendingLoop);

        await app.RunAsync("http://0.0.0.0:18080");
    }

    // Refuse les demandes d'accès restées sans réponse de l'owner
    static async Task ExpirePendingLoop()
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            try   { await SendAll(Lobby.ExpirePending()); }
            catch (Exception ex) { log.LogError(ex, "Expiration des demandes en attente"); }
        }
    }

    static async Task HandleConnection(WebSocket ws, IPAddress ip)
    {
        var me = new Conn(ws, ip);
        var limiter = new RateLimiter(RatePerSecond, RateBurst);
        int dropped = 0;

        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var json = await ReceiveFullMessage(ws);
                if (json == null) break;

                if (!limiter.TryTake())
                {
                    if (++dropped > MaxDroppedMessages)
                        throw new ClientViolation(WebSocketCloseStatus.PolicyViolation, "trop de messages");
                    continue;
                }

                var data = Protocol.Parse(json);
                if (data == null)
                {
                    await SendRaw(ws, new OutMsg { Type = MsgType.Error, Text = "message invalide" });
                    continue;
                }

                var error = Protocol.Validate(data);
                if (error != null)
                {
                    await SendRaw(ws, new OutMsg { Type = MsgType.Error, Text = error });
                    continue;
                }

                switch (data.Type)
                {
                    case MsgType.Join:
                        await SendAll(Lobby.Join(me, data, out var badCodeLimitReached));
                        if (badCodeLimitReached)
                            throw new ClientViolation(WebSocketCloseStatus.PolicyViolation, "trop de codes d'invitation invalides");
                        break;

                    case MsgType.Leave:
                        await SendAll(Lobby.Leave(me));
                        break;

                    case MsgType.JoinDecision:
                        await SendAll(Lobby.Decide(me, data.Name!, data.Accept!.Value));
                        break;

                    case MsgType.RoomSettings:
                        await SendAll(Lobby.UpdateSettings(me, data));
                        break;

                    case MsgType.Chat:
                        var (name, room, members) = Lobby.MembersOf(me);
                        if (name != null)
                        {
                            var chat = new OutMsg { Type = MsgType.Chat, Room = room, Text = $"{name}: {data.Text}" };
                            await SendAll(members.Select(m => new Outgoing(m, chat)).ToList());
                        }
                        break;

                    // Signaling WebRTC : routage unicast, uniquement à l'intérieur de la room de l'émetteur
                    case MsgType.Offer:
                    case MsgType.Answer:
                    case MsgType.IceCandidate:
                        var (from, target) = Lobby.FindMember(me, data.To!);
                        if (target != null)
                            await SendAll([new Outgoing(target, new OutMsg
                            {
                                Type      = data.Type.Value,
                                From      = from,
                                To        = data.To,
                                Sdp       = data.Sdp,
                                Candidate = data.Candidate
                            })]);
                        break;

                    case MsgType.ListRooms:
                        await SendRaw(ws, new OutMsg { Type = MsgType.Rooms, Rooms = Lobby.ListPublic() });
                        break;

                    case MsgType.Ping:
                        await SendRaw(ws, new OutMsg { Type = MsgType.Pong, Timestamp = data.Timestamp });
                        break;
                }
            }
        }
        catch (ClientViolation v)
        {
            log.LogWarning("Déconnexion de {Ip} : {Reason}", ip, v.Message);
            try { await ws.CloseOutputAsync(v.Status, v.Message, CancellationToken.None); } catch { }
        }
        catch (Exception) when (ws.State != WebSocketState.Open)
        {
            // Connexion fermée, coupée ou inactive trop longtemps : rien à signaler
        }
        catch (OperationCanceledException)
        {
            // Timeout d'inactivité
        }
        finally
        {
            // Déconnexion : retirer de la room (ou annuler la demande en attente), notifier, transférer ownership
            await SendAll(Lobby.Leave(me));
        }
    }

    // ------------------------------------------------------------------ //
    // CONNEXIONS
    // ------------------------------------------------------------------ //

    static bool TryAcquireConnectionSlot(IPAddress ip)
    {
        if (Interlocked.Increment(ref connectionCount) > MaxConnections)
        {
            Interlocked.Decrement(ref connectionCount);
            return false;
        }
        if (connectionsPerIp.AddOrUpdate(ip, 1, (_, n) => n + 1) > MaxConnectionsPerIp)
        {
            ReleaseConnectionSlot(ip);
            return false;
        }
        return true;
    }

    static void ReleaseConnectionSlot(IPAddress ip)
    {
        Interlocked.Decrement(ref connectionCount);
        if (connectionsPerIp.AddOrUpdate(ip, 0, (_, n) => n - 1) <= 0)
            connectionsPerIp.TryRemove(new KeyValuePair<IPAddress, int>(ip, 0));
    }

    sealed class ClientViolation(WebSocketCloseStatus status, string reason) : Exception(reason)
    {
        public WebSocketCloseStatus Status { get; } = status;
    }

    // Token bucket : RatePerSecond messages/s en régime établi, RateBurst en pointe
    sealed class RateLimiter(double perSecond, double burst)
    {
        readonly double capacity = burst;
        double tokens = burst;
        long   last   = Stopwatch.GetTimestamp();

        public bool TryTake()
        {
            var now = Stopwatch.GetTimestamp();
            tokens = Math.Min(capacity, tokens + Stopwatch.GetElapsedTime(last, now).TotalSeconds * perSecond);
            last   = now;
            if (tokens < 1) return false;
            tokens -= 1;
            return true;
        }
    }

    static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // null = fermeture propre par le client
    static async Task<string?> ReceiveFullMessage(WebSocket ws)
    {
        using var idle = new CancellationTokenSource(IdleTimeout);
        var buffer = new byte[4096];
        using var ms = new MemoryStream();
        ValueWebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buffer.AsMemory(), idle.Token);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            if (result.MessageType != WebSocketMessageType.Text)
                throw new ClientViolation(WebSocketCloseStatus.InvalidMessageType, "texte uniquement");
            if (ms.Length + result.Count > MaxMessageBytes)
                throw new ClientViolation(WebSocketCloseStatus.MessageTooBig, "message trop gros");
            ms.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        try   { return StrictUtf8.GetString(ms.GetBuffer(), 0, (int)ms.Length); }
        catch (DecoderFallbackException) { throw new ClientViolation(WebSocketCloseStatus.InvalidPayloadData, "UTF-8 invalide"); }
    }

    // ------------------------------------------------------------------ //
    // ENVOI
    // ------------------------------------------------------------------ //

    // Envoie les messages produits par Lobby (après relâchement de son verrou).
    // Un même OutMsg diffusé à toute la room n'est sérialisé qu'une fois.
    static async Task SendAll(List<Outgoing> outs)
    {
        var serialized = new Dictionary<OutMsg, byte[]>(ReferenceEqualityComparer.Instance);
        foreach (var o in outs)
        {
            if (!serialized.TryGetValue(o.Msg, out var bytes))
                serialized[o.Msg] = bytes = Protocol.Serialize(o.Msg);
            if (o.To.Ws.State != WebSocketState.Open) continue;
            try   { await SendBytes(o.To.Ws, bytes); }
            catch { /* connexion morte : son propre handler la retirera de la room */ }
        }
    }

    static Task SendRaw(WebSocket ws, OutMsg msg) => SendBytes(ws, Protocol.Serialize(msg));

    static async Task SendBytes(WebSocket ws, byte[] data)
    {
        if (!sendLocks.TryGetValue(ws, out var sendLock)) return; // connexion déjà terminée
        await sendLock.WaitAsync();
        try     { await ws.SendAsync(data, WebSocketMessageType.Text, true, CancellationToken.None); }
        finally { sendLock.Release(); }
    }
}
