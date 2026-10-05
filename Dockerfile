FROM node:24-bookworm-slim AS client
WORKDIR /src/client
COPY client/package*.json ./
RUN npm ci
COPY client/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS server
WORKDIR /src
COPY server/Argus.Server/Argus.Server.csproj server/Argus.Server/
RUN dotnet restore server/Argus.Server/Argus.Server.csproj
COPY server/ server/
COPY --from=client /src/server/Argus.Server/wwwroot/ server/Argus.Server/wwwroot/
RUN dotnet publish server/Argus.Server/Argus.Server.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=server /app/ ./
ENV ASPNETCORE_HTTP_PORTS=8080
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Argus.Server.dll"]
