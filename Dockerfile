FROM mcr.microsoft.com/dotnet/sdk:10.0-noble-aot AS build

WORKDIR /src

# Kopiere nur die csproj-Datei für effizientes Layer-Caching
COPY HashBackup/HashBackup.csproj HashBackup/

# Restore dependencies 
RUN dotnet restore HashBackup/HashBackup.csproj

# Kopiere den restlichen Code nachdem die Abhängigkeiten wiederhergestellt wurden
COPY . .

# Build the production Linux binary as a self-contained NativeAOT executable.
RUN dotnet publish HashBackup/HashBackup.csproj \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    /p:PublishAot=true \
    /p:PublishSingleFile=true \
    -o /app/publish

# Final stage - output binary is in /app/publish
FROM scratch AS output
COPY --from=build /app/publish /app/publish
