using UnityEngine;
using System.Collections.Generic;

// Exemple : synchronise les objets taggés "Player" en P2P (canal "state", binaire)
// et affiche les joueurs distants sous forme de cubes rouges.
public class MyYoloNetMgr : YoloNetMgr
{
    private float lastSend = 0f;
    public float SecondBetweenSend = 0.1f;

    // NetId réseau de chaque objet local synchronisé
    private readonly Dictionary<GameObject, long> localNetIds = new Dictionary<GameObject, long>();
    private readonly Dictionary<GameObject, ushort> localSeqs = new Dictionary<GameObject, ushort>();

    // Cubes des joueurs distants, par "pair/NetId"
    private readonly Dictionary<string, GameObject> remotePlayers = new Dictionary<string, GameObject>();
    private readonly Dictionary<string, ushort> remoteSeqs = new Dictionary<string, ushort>();

    protected override void Update()
    {
        base.Update(); // traite les files de YoloNetMgr → OnRcvTransform, OnPeerLeft...

        if ((Time.time - lastSend) < SecondBetweenSend)
            return;
        lastSend = Time.time;

        // Envoyer les positions des joueurs locaux (envoi groupé en LateUpdate par YoloNetMgr)
        foreach (GameObject obj in GameObject.FindGameObjectsWithTag("Player"))
        {
            if (!localNetIds.TryGetValue(obj, out long netId))
                localNetIds[obj] = netId = NetworkEntityIdGenerator.AllocateId();

            localSeqs.TryGetValue(obj, out ushort seq);
            localSeqs[obj] = ++seq;

            TransformBroadcaster.Queue(new TransformState
            {
                NetId       = netId,
                Seq         = seq,
                PrefabType  = "Player",
                HasPosition = true,
                Position    = obj.transform.position,
            });
        }
    }

    public override void OnRcvTransform(string from, TransformState state)
    {
        string key = from + "/" + state.NetId;

        // Ignorer un état plus ancien que le dernier appliqué
        if (remoteSeqs.TryGetValue(key, out ushort last) && !TransformState.IsNewer(state.Seq, last))
            return;
        remoteSeqs[key] = state.Seq;

        if (!remotePlayers.TryGetValue(key, out GameObject remotePlayer) || remotePlayer == null)
        {
            // Créer le cube distant, en rouge
            remotePlayer = GameObject.CreatePrimitive(PrimitiveType.Cube);
            remotePlayer.name = key;
            Renderer renderer = remotePlayer.GetComponent<Renderer>();
            if (renderer != null)
                renderer.material.color = Color.red;
            remotePlayers[key] = remotePlayer;
        }

        if (state.HasPosition)
            remotePlayer.transform.position = state.Position;
    }

    public override void OnPeerLeft(string peer)
    {
        var prefix = peer + "/";
        var gone = new List<string>();
        foreach (var key in remotePlayers.Keys)
            if (key.StartsWith(prefix)) gone.Add(key);
        foreach (var key in gone)
        {
            if (remotePlayers[key] != null) Destroy(remotePlayers[key]);
            remotePlayers.Remove(key);
            remoteSeqs.Remove(key);
        }
    }

    public override void OnServerError(string error)
    {
        Debug.LogWarning("Lobby : " + error);
    }

    // Appelé seulement si autoRoom.Options.AutoAccept = false : brancher ici une UI de validation
    public override void OnJoinRequest(string player)
    {
        Debug.Log($"{player} demande à entrer : valider avec ProgramClient.AnswerJoinRequest(player, true/false)");
    }

    public override void OnRoomJoined(RoomState room)
    {
        Debug.Log($"Room '{room.Name}' ({room.Visibility}, {room.Players}/{room.MaxPlayers})"
                  + (room.Code != null ? $" — code d'invitation : {room.Code}" : ""));
    }
}
