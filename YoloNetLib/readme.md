# YoloNetLib pour CSharp et Unity



## Ajout des dépendences

### Packages nuget

https://www.nuget.org/packages/SIPSorcery


## Utilisation 


Le Client se connecte à un serveur TURN (ysh2.yolospacehacker.com:3478)
  Ce serveur identifie l'IP/port publique utilisé pour le joindre.
  Le client connait ainsi ses IP/port en local et en public sur Internet.
  Le protocole support est UDP

Le client se connecte ensuite à un serveur de Lobby en websocket sécurisée (wss://ysh2.yolospacehacker.com/ws, via Traefik)
  Il demande la liste des rooms.
  S'il y en a, il JOIN la première
  Sinon, il en créé une, et devient le owner de la room.
  Il permet de :
    lister les clients de la room, 
    identifier le owner, 
    envoyer des messages à un client, au owner, à tous,
    


### CSharp 



### Canaux P2P

Chaque paire de joueurs ouvre deux DataChannels WebRTC :
- `lobby` : chat et événements d'entités (JSON : création, destruction, attributs).
- `state` : positions/rotations en binaire compact (`NetTransform.cs`), environ 40 octets par entité,
  toutes les entités d'un joueur regroupées dans un seul paquet par frame.

`NESynchro` n'envoie que si l'objet a bougé, plus un état de maintien par seconde.
Pour envoyer vos propres transforms : `TransformBroadcaster.Queue(...)`, l'envoi est fait en `LateUpdate` par le manager.
Côté réception : surcharger `YoloNetMgr.OnRcvTransform(from, state)`.

### Rooms

Le créateur d'une room en devient le propriétaire (owner). Réglages à la création (`RoomOptions`) :
- `Visibility` : `Public` (listée par `ListRooms()`) ou `Private` (absente de la liste, entrée avec le code d'invitation `Room.Code`) ;
- `MaxPlayers` : de 2 à 16 ;
- `AutoAccept` : le lobby soumet chaque arrivée à l'owner. Avec `true` (par défaut), son client accepte automatiquement.
  Avec `false`, le jeu reçoit `OnJoinRequest` et répond avec `AnswerJoinRequest` ; sans réponse, la demande expire au bout de 60 s.
  Ce réglage est local : il s'applique aussi si le joueur devient owner plus tard (`ProgramClient.AutoAcceptJoinRequests`).

Au démarrage, le manager rejoint la room réglée dans son champ `autoRoom` (inspecteur Unity).
Si `RoomName` est vide, il rejoint la première room publique qui n'est pas pleine, sinon il crée `room_<pseudo>`.

```csharp
var rooms = await ProgramClient.ListRooms();          // name, players, max_players, owner
await ProgramClient.CreateRoom("ma_partie", new RoomOptions { Visibility = RoomVisibility.Private, MaxPlayers = 4, AutoAccept = false });
await ProgramClient.JoinRoom("ma_partie", "ABCD2345"); // null = entré, sinon raison du refus (attend la réponse de l'owner)
await ProgramClient.UpdateRoomSettings(visibility: RoomVisibility.Public);   // owner
```

Après une coupure, le client se reconnecte avec un jeton fourni par le lobby : il n'a besoin ni du code ni d'une nouvelle validation.
