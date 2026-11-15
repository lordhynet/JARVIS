FROM mcr.microsoft.com/dotnet/sdk:11.0@sha256:65f3c7c6a25a239ab3e6719f2896147e7b68242ff2d8818dd70498bde6ef8bea AS build
WORKDIR /source
COPY Directory.Build.props global.json ./
COPY appsettings.example.json ./appsettings.json
COPY appsettings.example.json ./appsettings.example.json
COPY src/ ./src/
RUN dotnet publish src/Jarvis.Api/Jarvis.Api.csproj -c Release -o /app --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:11.0@sha256:bc0d6653979efba491ab3d6f589d691f96c309c3c3ef81325efa6978e7b7f242
ARG FFMPEG_VERSION=7:8.0.1-3ubuntu2
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg=${FFMPEG_VERSION} && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_HTTP_PORTS=8080
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Jarvis.Api.dll"]
