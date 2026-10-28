using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using SIPSorcery.Net;
using UnityEngine;

record TurnInfo(string Name, List<string> Candidates, string Sdp);

// Serveur ICE reçu du lobby dans room_info
class IceServerInfo
{
    [JsonPropertyName("urls")]       public string Urls       { get; set; }
    [JsonPropertyName("username")]   public string Username   { get; set; }
    [JsonPropertyName("credential")] public string Credential { get; set; }
}

class TurnIf
{
    // Le secret coturn reste sur le serveur : le lobby envoie dans room_info des identifiants
    // TURN éphémères (username = "<expiration>:<nom>", credential = HMAC du secret).
    static volatile List<RTCIceServer> iceServers = new List<RTCIceServer>();

    public static void SetIceServers(List<IceServerInfo> servers)
    {
        var list = new List<RTCIceServer>();
        if (servers != null)
            foreach (var s in servers)
                if (!string.IsNullOrEmpty(s?.Urls))
                    list.Add(new RTCIceServer { urls = s.Urls, username = s.Username, credential = s.Credential });

        iceServers = list;
        Debug.Log(list.Count > 0
            ? $"[TURN] {list.Count} serveur(s) ICE reçu(s) du lobby"
            : "[TURN] aucun serveur ICE reçu : connexions directes uniquement (pas de STUN/TURN)");
    }

    // Réutilisable par le signaling client pour créer des RTCPeerConnection
    // ICE prioritise les candidats dans l'ordre : host > srflx > relay
    public static RTCConfiguration GetRTCConfiguration() =>
        new RTCConfiguration { iceServers = new List<RTCIceServer>(iceServers) };

    // Usage diagnostique : collecte complète puis retourne les candidats
    public static async Task<TurnInfo> GetTurnConfig(string clientName)
    {
        var pc         = new RTCPeerConnection(GetRTCConfiguration());
        var candidates = new List<string>();
        var done       = new TaskCompletionSource<bool>();

        pc.onicecandidate += cand =>
        {
            if (cand != null) candidates.Add(cand.candidate);
            else              done.TrySetResult(true);
        };

        pc.onicegatheringstatechange += state =>
        {
            if (state == RTCIceGatheringState.complete)
                done.TrySetResult(true);
        };

        var offer = pc.createOffer();
        await pc.setLocalDescription(offer);

        await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(10)));

        pc.close();
        return new TurnInfo(clientName, candidates, offer.sdp);
    }
}
