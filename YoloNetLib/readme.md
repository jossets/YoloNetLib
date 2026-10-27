# YoloNetLib pour CSharp et Unity



## Ajout des dépendences

### Packages nuget

https://www.nuget.org/packages/SIPSorcery


## Utilisation 


Le Client se connecte à un serveur TURN (ysh2.yolospacehacker.com:3478)
  Ce serveur identifie l'IP/port publique utilisé pour le joindre.
  Le client connait ainsi ses IP/port en local et en public sur Internet.
  Le protocole support est UDP

Le client se connecte ensuite à un serveur de Lobby en websocket (ws://ysh2.yolospacehacker.com:18080/ws)
  Il demande la liste des rooms.
  S'il y en a, il JOIN la première
  Sinon, il en créé une, et devient le owner de la room.
  Il permet de :
    lister les clients de la room, 
    identifier le owner, 
    envoyer des messages à un client, au owner, à tous,
    


### CSharp 


