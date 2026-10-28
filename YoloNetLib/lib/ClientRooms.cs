using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

// public  : listée par ListRooms()
// private : absente de la liste ; il faut le code d'invitation (Room.Code) pour entrer
public enum RoomVisibility { Public, Private }

// Room publique renvoyée par ListRooms()
public class RoomSummary
{
    [JsonPropertyName("name")]        public string Name         { get; set; }
    [JsonPropertyName("players")]     public int    Players      { get; set; }
    [JsonPropertyName("max_players")] public int    MaxPlayers   { get; set; }
    [JsonPropertyName("owner")]       public string Owner        { get; set; }

    [JsonIgnore] public bool IsFull => Players >= MaxPlayers;
}

// Réglages d'une room à créer (Visibility et MaxPlayers ignorés si la room existe déjà)
[Serializable]
public class RoomOptions
{
    public RoomVisibility Visibility = RoomVisibility.Public;
    [Range(2, 16)] public int MaxPlayers = 16;
    // Le lobby soumet chaque arrivée au propriétaire de la room.
    // true : ce client accepte tout le monde automatiquement ; false : OnJoinRequest, le jeu répond avec AnswerJoinRequest.
    // Réglage local : s'applique aussi si ce joueur devient propriétaire plus tard.
    public bool AutoAccept = true;
}

// Room courante (nouvel objet à chaque mise à jour : lisible depuis n'importe quel thread)
public class RoomState
{
    public string         Name;
    public string         Owner;
    public RoomVisibility Visibility;
    public int            MaxPlayers;
    public int            Players;
    public string         Code;        // code d'invitation à partager (room privée uniquement)
}

// Room à rejoindre au démarrage par AutoClient
[Serializable]
public class AutoRoomRequest
{
    public string      RoomName   = "";     // vide : rejoindre une room publique non pleine, sinon en créer une
    public string      InviteCode = "";     // pour rejoindre une room privée existante
    public RoomOptions Options    = new RoomOptions();
}

static class RoomWire
{
    public static string ToWire(RoomVisibility v) => v == RoomVisibility.Private ? "private" : "public";
    public static RoomVisibility ParseVisibility(string s) => s == "private" ? RoomVisibility.Private : RoomVisibility.Public;
}

// ------------------------------------------------------------------ //
// ROOMS : liste, création, accès (code / validation par l'owner), réglages
// ------------------------------------------------------------------ //
partial class ProgramClient
{
    public static RoomState       Room     { get; private set; }   // null hors room
    public static AutoRoomRequest AutoRoom = new AutoRoomRequest();

    // Propriétaire : accepter automatiquement les demandes d'entrée (sinon OnJoinRequest).
    // Pris dans RoomOptions.AutoAccept à la création / au join ; modifiable à tout moment.
    public static volatile bool AutoAcceptJoinRequests = true;

    // Événements (thread réseau : repasser sur le thread Unity avant d'appeler l'API Unity)
    public static event Action<RoomState> OnRoomJoined;
    public static event Action<RoomState> OnRoomUpdated;
    public static event Action<string>    OnJoinPending;            // room : demande envoyée à l'owner
    public static event Action<string>    OnJoinRejected;           // raison
    public static event Action<string>    OnJoinRequest;            // owner, si AutoAcceptJoinRequests == false : joueur qui demande à entrer
    public static event Action<string>    OnJoinRequestCancelled;   // owner : demande retirée ou expirée

    static string      roomToken;     // jeton de reconnexion remis par le lobby
    static string      joinCode;      // code utilisé pour entrer (reconnexion si le jeton a expiré)
    static RoomOptions joinOptions;   // réglages demandés (si la room doit être recréée à la reconnexion)

    static volatile TaskCompletionSource<List<RoomSummary>> pendingRoomsList;
    static volatile TaskCompletionSource<string>            pendingJoin;
    static volatile bool                                    joinIsPending;

    static readonly TimeSpan ReplyTimeout    = TimeSpan.FromSeconds(5);
    static readonly TimeSpan ApprovalTimeout = TimeSpan.FromSeconds(75);   // validation manuelle : > délai d'expiration côté lobby (60 s)

    // ------------------------------------------------------------------ //
    // API
    // ------------------------------------------------------------------ //

    // Rooms publiques ; null si le lobby ne répond pas
    public static async Task<List<RoomSummary>> ListRooms()
    {
        var tcs = new TaskCompletionSource<List<RoomSummary>>(TaskCreationOptions.RunContinuationsAsynchronously);
        pendingRoomsList = tcs;
        await Send(new Msg { Type = "list_rooms" });
        var done = await Task.WhenAny(tcs.Task, Task.Delay(ReplyTimeout, cts.Token));
        if (pendingRoomsList == tcs) pendingRoomsList = null;
        return done == tcs.Task ? tcs.Task.Result : null;
    }

    // Rejoint une room existante (code requis si elle est privée).
    // null = dans la room ; sinon la raison du refus. Attend la réponse de l'owner (immédiate s'il est en AutoAccept).
    public static Task<string> JoinRoom(string room, string inviteCode = null) =>
        JoinOrCreate(room, inviteCode, null);

    // Crée la room avec ces réglages (ou la rejoint si elle existe déjà)
    public static Task<string> CreateRoom(string room, RoomOptions options) =>
        JoinOrCreate(room, null, options ?? new RoomOptions());

    public static async Task LeaveRoom()
    {
        await Send(new Msg { Type = "leave" });
        currentRoom = null;
        Room        = null;
        roomToken   = null;
        CloseAllPeers();
    }

    // Owner : réponse à OnJoinRequest (validation manuelle)
    public static Task AnswerJoinRequest(string playerName, bool accept) =>
        Send(new Msg { Type = "join_decision", Name = playerName, Accept = accept });

    // Owner : changer les réglages (null = inchangé)
    public static Task UpdateRoomSettings(RoomVisibility? visibility = null, int? maxPlayers = null) =>
        Send(new Msg
        {
            Type       = "room_settings",
            Visibility = visibility.HasValue ? RoomWire.ToWire(visibility.Value) : null,
            MaxPlayers = maxPlayers,
        });

    // ------------------------------------------------------------------ //
    // JOIN
    // ------------------------------------------------------------------ //

    static async Task<string> JoinOrCreate(string room, string inviteCode, RoomOptions options)
    {
        room = SanitizeRoomName(room);
        if (string.IsNullOrEmpty(room)) return "nom de room vide";

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        pendingJoin   = tcs;
        joinIsPending = false;
        joinCode      = string.IsNullOrEmpty(inviteCode) ? null : inviteCode.Trim().ToUpperInvariant();
        joinOptions   = options;
        if (options != null) AutoAcceptJoinRequests = options.AutoAccept;
        await Send(BuildJoin(room, joinCode, options, token: null));

        // Réponse attendue sous 5 s ; si l'owner valide à la main, on attend sa décision
        var start = DateTime.UtcNow;
        while (!cts.IsCancellationRequested)
        {
            var limit = joinIsPending ? ApprovalTimeout : ReplyTimeout;
            if (DateTime.UtcNow - start > limit) break;
            if (await Task.WhenAny(tcs.Task, Task.Delay(250)) == tcs.Task)
            {
                if (pendingJoin == tcs) pendingJoin = null;
                return tcs.Task.Result;
            }
        }
        if (pendingJoin == tcs) pendingJoin = null;
        return joinIsPending ? "pas de réponse du propriétaire" : "pas de réponse du lobby";
    }

    static Msg BuildJoin(string room, string code, RoomOptions options, string token) => new Msg
    {
        Type       = "join",
        Name       = myName,
        Room       = room,
        Code       = code,
        Token      = token,
        Visibility = options != null ? RoomWire.ToWire(options.Visibility) : null,
        MaxPlayers = options?.MaxPlayers,
    };

    // Reconnexion : le jeton évite de redemander le code ou la validation ; les réglages recréent la room si elle a été fermée
    static Msg RejoinMessage() => BuildJoin(currentRoom, joinCode ?? Room?.Code, joinOptions, roomToken);

    // ------------------------------------------------------------------ //
    // MODE AUTO : room configurée, sinon 1re room publique non pleine, sinon création
    // ------------------------------------------------------------------ //

    static async Task AutoConnectFlow()
    {
        var request = AutoRoom ?? new AutoRoomRequest();
        AutoAcceptJoinRequests = request.Options?.AutoAccept ?? true;
        string error;

        if (!string.IsNullOrEmpty(request.RoomName))
        {
            Log($"[AUTO] Room configurée : '{request.RoomName}'");
            error = await JoinOrCreate(request.RoomName, request.InviteCode, request.Options);
            if (error != null) Log($"[AUTO] Impossible de rejoindre '{request.RoomName}' : {error}");
            return;
        }

        Log("[AUTO] Demande de la liste des rooms...");
        var rooms = await ListRooms();
        if (rooms == null)
        {
            Log("[AUTO] Timeout : pas de réponse du serveur.");
            return;
        }

        var target = rooms.FirstOrDefault(r => !r.IsFull);
        if (target != null)
        {
            Log($"[AUTO] Room trouvée → rejoins '{target.Name}' ({target.Players}/{target.MaxPlayers})");
            error = await JoinRoom(target.Name);
            if (error == null)
            {
                await Send(new Msg { Type = "chat", Name = myName, Room = target.Name, Text = "bonjour" });
                return;
            }
            Log($"[AUTO] Impossible de rejoindre '{target.Name}' ({error}) → création d'une room");
        }

        var ownRoom = $"room_{myName}";
        Log($"[AUTO] Création de '{ownRoom}'");
        error = await CreateRoom(ownRoom, request.Options);
        if (error != null)
            Log($"[AUTO] Impossible de rejoindre une room : {error}");
    }

    // ------------------------------------------------------------------ //
    // MESSAGES DU LOBBY LIÉS AUX ROOMS (appelé par ReceiveLoop)
    // ------------------------------------------------------------------ //

    static void HandleRoomMessage(Msg data)
    {
        switch (data.Type)
        {
            case "room_info":
                // Le serveur peut nous avoir renommé si le nom était déjà pris (ex: Bob → Bob_2)
                if (!string.IsNullOrEmpty(data.Name) && data.Name != myName)
                {
                    Log($"[ROOM] Nom déjà pris → renommé en {data.Name}");
                    myName = data.Name;
                }
                // Identifiants STUN/TURN éphémères : à poser avant les offers des pairs, qui suivent ce message
                TurnIf.SetIceServers(data.IceServers);
                currentRoom = data.Room;
                roomOwner   = data.Owner;
                roomToken   = data.Token ?? roomToken;
                Room        = ToState(data, null);
                joinIsPending = false;
                pendingJoin?.TrySetResult(null);
                Log(AmIRoomOwner() ? $"[ROOM] {data.Room} — propriétaire" : $"[ROOM] {data.Room} — owner: {data.Owner}");
                if (Room.Code != null) Log($"[ROOM] Room privée — code d'invitation : {Room.Code}");
                OnRoomJoined?.Invoke(Room);
                break;

            case "room_updated":
                Room = ToState(data, Room);
                Log($"[ROOM] Réglages : {data.Visibility}, {data.Players}/{data.MaxPlayers}");
                OnRoomUpdated?.Invoke(Room);
                break;

            case "owner_changed":
                roomOwner = data.Owner;
                if (Room != null) Room = ToState(new Msg { Owner = data.Owner }, Room);
                Log(AmIRoomOwner() ? $"[ROOM] Vous êtes le nouveau propriétaire de '{data.Room}'" : $"[ROOM] {data.Text}");
                break;

            case "join_pending":
                joinIsPending = true;
                Log($"[ROOM] '{data.Room}' : en attente de la réponse du propriétaire");
                OnJoinPending?.Invoke(data.Room);
                break;

            case "join_rejected":
                Debug.LogWarning($"[ROOM] Accès à '{data.Room}' refusé : {data.Text}");
                OnJoinRejected?.Invoke(data.Text ?? "");
                pendingJoin?.TrySetResult(data.Text ?? "refusé");
                break;

            case "join_request":
                if (AutoAcceptJoinRequests)
                {
                    Log($"[ROOM] {data.Name} demande à rejoindre la room → accepté automatiquement");
                    _ = AnswerJoinRequest(data.Name, true);
                }
                else
                {
                    Log($"[ROOM] {data.Name} demande à rejoindre la room");
                    OnJoinRequest?.Invoke(data.Name);
                }
                break;

            case "join_request_cancelled":
                OnJoinRequestCancelled?.Invoke(data.Name);
                break;

            case "rooms":
                var waiter = pendingRoomsList;
                pendingRoomsList = null;
                waiter?.TrySetResult(data.Rooms ?? new List<RoomSummary>());
                break;

            case "error":
                // Message refusé par le lobby (pseudo invalide, room pleine, code faux, message trop gros...)
                Debug.LogWarning($"[LOBBY] Refusé : {data.Text}");
                OnServerError?.Invoke(data.Text ?? "");
                pendingJoin?.TrySetResult(data.Text ?? "erreur");
                break;
        }
    }

    // Fusionne les champs présents dans le message avec l'état précédent
    static RoomState ToState(Msg m, RoomState previous) => new RoomState
    {
        Name       = m.Room ?? previous?.Name,
        Owner      = m.Owner ?? previous?.Owner,
        Visibility = m.Visibility != null ? RoomWire.ParseVisibility(m.Visibility) : previous?.Visibility ?? RoomVisibility.Public,
        MaxPlayers = m.MaxPlayers ?? previous?.MaxPlayers ?? 0,
        Players    = m.Players    ?? previous?.Players    ?? 0,
        // Le code n'existe que pour une room privée : un room_updated public l'efface
        Code       = m.Visibility != null ? m.Code : (m.Code ?? previous?.Code),
    };
}
