FROM mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim AS build
WORKDIR /src

COPY backend/NuGet.Config backend/
COPY backend/AudioChoice.Api/AudioChoice.Api.csproj backend/AudioChoice.Api/
RUN dotnet restore backend/AudioChoice.Api/AudioChoice.Api.csproj \
    -p:EnablePostgres=true \
    --configfile backend/NuGet.Config

COPY backend/AudioChoice.Api/ backend/AudioChoice.Api/
COPY backend/Database/ backend/Database/
RUN dotnet publish backend/AudioChoice.Api/AudioChoice.Api.csproj \
    -p:EnablePostgres=true \
    --configuration Release \
    --no-restore \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim AS runtime
RUN apt-get update \
    && apt-get install --yes --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /data/uploads /data/transcripts \
    && chown -R app:app /data

WORKDIR /app
COPY --from=build --chown=app:app /app/publish .

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0 \
    AudioChoice__DataPath=/data \
    AudioChoice__OpenAI__WorkerEnabled=false

EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "AudioChoice.Api.dll"]
