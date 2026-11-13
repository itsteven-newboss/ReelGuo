# Build context: backend/
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/ReelGuo.Worker/ReelGuo.Worker.csproj src/ReelGuo.Worker/
COPY src/ReelGuo.Data/ReelGuo.Data.csproj src/ReelGuo.Data/
RUN dotnet restore src/ReelGuo.Worker/ReelGuo.Worker.csproj
COPY src/ReelGuo.Worker/ src/ReelGuo.Worker/
COPY src/ReelGuo.Data/ src/ReelGuo.Data/
RUN dotnet publish src/ReelGuo.Worker/ReelGuo.Worker.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0
# ffmpeg/ffprobe for transcoding (Debian package; includes zscale/tonemap/loudnorm).
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
# Shared media volume (filesystem storage) must be writable by the non-root app user.
RUN mkdir -p /data/storage && chown -R $APP_UID /data
USER $APP_UID
ENTRYPOINT ["dotnet", "ReelGuo.Worker.dll"]
