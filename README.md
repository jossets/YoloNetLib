# YoloNetLib
Librairie temps réel multi joueur


## Server

Installer coturn (écoute sur le port 3478) et lobby_server (ws sur le port 18080) sur le serveur.

Copier turnserver.conf.default en turnserver.conf
Mettre à jour fqdn et secret dans turnserver.conf 

```
cd coturn
docker compose up -d 

cd ../lobby_server 
./start.sh 
```


## Client 

Dans Unity:
Ajouter le script MyYoloNetMgr sur un objet.
Copier le répertoire Assets/plugins dans le répertoire Assets du projet.

Dans turn.cs mettre à jour fqdn et secret.
class TurnIf
{
    const string TurnSecret  = "COTURN_SECRET";
    const string TurnHost    = "host";
    ..
}

Le nom de l'utilisateur est, dans l'ordre :
- généré aléatoirement
- pris dans le paramètre User Name du script
- pris dans le ficheir de config 
- pris en ligne de commande du .exe 

Build l'exe unity dans le répertoire win64.
Mettre à jour le nom du client dans config.cfg
Lancer avec les go_yop_01.bat... 


Le client se connecte au lobby au startup et récupère les rooms disponibles.




