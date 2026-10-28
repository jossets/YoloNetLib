# YoloNetLib
Librairie temps réel multi joueur


## Server

Tout tourne en Docker, depuis le répertoire `coturn` :
- traefik : wss://LOBBY_DOMAIN/ws (ports 80/443, certificat Let's Encrypt)
- lobby : lobby_server, joignable uniquement via Traefik (plus de port 18080 exposé)
- coturn : STUN/TURN sur le port 3478, relais UDP 50000-51000

Ports à ouvrir sur le pare-feu : TCP 80, 443, 3478 et UDP 3478, 50000-51000.

Copier .env.example en .env, renseigner LOBBY_DOMAIN et ACME_EMAIL.
Copier turnserver.conf.default en turnserver.conf, mettre à jour fqdn et secret
(secret aléatoire, par ex. `openssl rand -hex 32`). C'est le seul endroit où le secret est défini :
le lobby le lit dans ce fichier pour générer des identifiants TURN éphémères (12 h) qu'il envoie aux clients.

```
cd coturn
docker compose up -d --build --pull always
```


## Client 

Dans Unity:
Ajouter le script MyYoloNetMgr sur un objet.
Copier le répertoire Assets/plugins dans le répertoire Assets du projet.

Dans client.cs, `serverUrl` pointe vers le lobby (wss://LOBBY_DOMAIN/ws).
Rien d'autre à configurer : les serveurs STUN/TURN et leurs identifiants sont reçus du lobby à chaque join.

Le pseudo et le nom de room n'acceptent que `A-Z a-z 0-9 _ -` (32 caractères max).

Le nom de l'utilisateur est, dans l'ordre :
- généré aléatoirement
- pris dans le paramètre User Name du script
- pris dans le ficheir de config 
- pris en ligne de commande du .exe 

Build l'exe unity dans le répertoire win64.
Mettre à jour le nom du client dans config.cfg
Lancer avec les go_yop_01.bat... 


Le client se connecte au lobby au startup et récupère les rooms disponibles.




