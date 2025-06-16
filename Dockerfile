FROM --platform=linux/amd64 mcr.microsoft.com/dotnet/sdk:9.0-bookworm-slim AS build

# Install required packages for compilation
RUN apt-get update && apt-get install -y \
    clang \
    gcc \
    g++ \
    lld \
    zlib1g-dev \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /src

# Kopiere nur die csproj-Datei für effizientes Layer-Caching
COPY HashBackup/HashBackup.csproj HashBackup/

# Restore dependencies 
RUN dotnet restore HashBackup/HashBackup.csproj

# Kopiere den restlichen Code nachdem die Abhängigkeiten wiederhergestellt wurden
COPY . .

# Build with ReadyToRun instead of AOT for better compatibility
RUN dotnet publish HashBackup/HashBackup.csproj \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    /p:PublishTrimmed=true \
    /p:PublishReadyToRun=true \
    /p:PublishSingleFile=true \
    -o /app/publish

# Final stage - output binary is in /app/publish
FROM scratch AS output
COPY --from=build /app/publish /app/publish
