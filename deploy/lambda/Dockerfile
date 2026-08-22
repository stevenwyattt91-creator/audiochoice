FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /source
COPY backend/ backend/
RUN dotnet restore backend/AudioChoice.Api/AudioChoice.Api.csproj -p:EnablePostgres=true
RUN dotnet publish backend/AudioChoice.Api/AudioChoice.Api.csproj -c Release -o /app -p:EnablePostgres=true --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
RUN apt-get update && apt-get install --yes --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app ./
ENTRYPOINT ["dotnet", "AudioChoice.Api.dll"]
