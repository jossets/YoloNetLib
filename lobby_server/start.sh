#!/bin/bash

cd /home/ubuntu/lobby_server || exit 1

nohup dotnet run > lobby.log 2>&1 &

echo "Serveur lancé. PID : $!"
