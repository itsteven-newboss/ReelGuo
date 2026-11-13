# Build context: backend/
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/ReelGuo.Api/ReelGuo.Api.csproj src/ReelGuo.Api/
COPY src/ReelGuo.Data/ReelGuo.Data.csproj src/ReelGuo.Data/
RUN dotnet restore src/ReelGuo.Api/ReelGuo.Api.csproj
COPY src/ReelGuo.Api/ src/ReelGuo.Api/
COPY src/ReelGuo.Data/ src/ReelGuo.Data/
# The OpenAPI document is generated during normal builds and committed; skip it here.
RUN dotnet publish src/ReelGuo.Api/ReelGuo.Api.csproj -c Release -o /app --no-restore -p:OpenApiGenerateDocuments=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
# Shared media volume (filesystem storage) must be writable by the non-root app user.
RUN mkdir -p /data/storage && chown -R $APP_UID /data
USER $APP_UID
ENTRYPOINT ["dotnet", "ReelGuo.Api.dll"]
