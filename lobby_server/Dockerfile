# Image du lobby. Reconstruire avec --pull pour récupérer les correctifs .NET 10.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY lobbytest.csproj .
RUN dotnet restore
COPY *.cs .
RUN dotnet publish -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# Utilisateur non-root fourni par l'image aspnet
USER $APP_UID
EXPOSE 18080
ENTRYPOINT ["dotnet", "lobbytest.dll"]
