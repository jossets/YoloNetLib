using UnityEngine;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;

public class MyYoloNetMgr : YoloNetMgr
{
    private float lastSend = 0f;
    public float SecondBetweenSend = 1.0f;

    // Dernière position reçue par joueur distant (écrite par le thread réseau).
    // Seule la plus récente compte : les positions intermédiaires sont écrasées.
    private readonly ConcurrentDictionary<string, Vector3> latestPositions =
        new ConcurrentDictionary<string, Vector3>();

    // Cache des cubes distants, évite un GameObject.Find par message.
    private readonly Dictionary<string, GameObject> remotePlayers =
        new Dictionary<string, GameObject>();

    void Update()
    {
        // 1. Appliquer la dernière position connue de chaque joueur distant.
        foreach (string remoteObjName in latestPositions.Keys)
        {
            if (!latestPositions.TryRemove(remoteObjName, out Vector3 remotePosition))
                continue;

            if (!remotePlayers.TryGetValue(remoteObjName, out GameObject remotePlayer) || remotePlayer == null)
            {
                // Créer le cube distant.
                remotePlayer = GameObject.CreatePrimitive(PrimitiveType.Cube);
                remotePlayer.name = remoteObjName;

                // Appliquer la couleur rouge.
                Renderer renderer = remotePlayer.GetComponent<Renderer>();
                if (renderer != null)
                    renderer.material.color = Color.red;

                remotePlayers[remoteObjName] = remotePlayer;
            }

            // Mettre à jour sa position.
            remotePlayer.transform.position = remotePosition;
        }

        // 2. Envoyer les positions toutes les secondes.
        if ((Time.time - lastSend) > SecondBetweenSend)
        {
            // Envoyer l'heure.
            ProgramClient.Send(new Msg
            {
                Type = "chat",
                Name = ProgramClient.myName,
                Room = ProgramClient.currentRoom,
                Text = "TIME " + Time.time.ToString()
            });

            // Envoyer les positions des joueurs locaux.
            GameObject[] objs =
                GameObject.FindGameObjectsWithTag("Player");

            foreach (GameObject obj in objs)
            {
                Vector3 position = obj.transform.position;

                NEVect3D pos = new NEVect3D(
                    position.x,
                    position.y,
                    position.z
                );

                string json_pos = JsonSerializer.Serialize(pos);

                ProgramClient.Send(new Msg
                {
                    Type = "chat",
                    Name = ProgramClient.myName,
                    Room = ProgramClient.currentRoom,
                    Text = "OBJPOS " + obj.name + " " + json_pos
                });
            }

            lastSend = Time.time;
        }
    }

    public override void OnRcvChat(string _from, string _text)
    {
        string from = _from;
        string text = _text;

        // Si l'émetteur est inclus dans le texte, l'extraire.
        if (from == "")
        {
            int index = text.IndexOf(":");

            if (index != -1)
            {
                from = text.Substring(0, index).Trim();
                text = text.Substring(index + 1).Trim();
            }
        }

        // Ignorer les messages de notre propre joueur.
        if (from == ProgramClient.myName)
            return;

        // Ne traiter que les messages de position du joueur.
        const string prefix = "OBJPOS Player ";

        if (!text.StartsWith(prefix))
            return;

        string json = text.Substring(prefix.Length).Trim();

        // Désérialisation hors des opérations Unity.
        NEVect3D pos;

        try
        {
            pos = JsonSerializer.Deserialize<NEVect3D>(json);
        }
        catch (JsonException ex)
        {
            Debug.LogWarning("Position JSON invalide : " + ex.Message);
            return;
        }

        if (pos == null)
            return;

        // Écrase la position précédente non encore appliquée ; Update() l'appliquera.
        latestPositions[from + "_Player"] = pos.GetVector3();
    }

    public class NEVect3D
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }

        public NEVect3D()
        {
        }

        public NEVect3D(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public Vector3 GetVector3()
        {
            return new Vector3(X, Y, Z);
        }
    }
}
