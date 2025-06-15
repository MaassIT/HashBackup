#!/bin/bash
set -e

# Farben für die Ausgabe
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

echo -e "${YELLOW}HashBackup Build-Skript${NC}"
echo "------------------------------"

# Verzeichnis zum Speichern der Builds
BUILD_DIR="builds"
mkdir -p $BUILD_DIR

# 1. macOS Version kompilieren
echo -e "${GREEN}Kompiliere macOS Version...${NC}"
dotnet publish HashBackup/HashBackup.csproj \
    -c Release \
    -r osx-x64 \
    --self-contained true \
    /p:PublishTrimmed=true \
    /p:PublishReadyToRun=true \
    /p:PublishSingleFile=true

# Kompilat in den Build-Ordner kopieren
echo "Kopiere macOS-Kompilat in $BUILD_DIR..."
cp -f HashBackup/bin/Release/net9.0/osx-x64/publish/HashBackup $BUILD_DIR/HashBackup-macos

# 2. Linux Version mit Docker kompilieren
echo -e "${GREEN}Kompiliere Linux Version mit Docker...${NC}"

# Docker-Image bauen
docker build --platform linux/amd64 -t hashbackup-linux-build .

# Binary aus dem Container extrahieren
echo "Extrahiere Linux-Kompilat aus Docker-Container..."
# Ein Kommando muss beim Erstellen des Containers angegeben werden (hier: echo als Dummy-Befehl)
docker create --name temp-hashbackup-container hashbackup-linux-build echo
docker cp temp-hashbackup-container:/app/publish/HashBackup $BUILD_DIR/HashBackup-linux
docker rm temp-hashbackup-container

# Berechtigungen setzen
chmod +x $BUILD_DIR/HashBackup-macos
chmod +x $BUILD_DIR/HashBackup-linux

echo -e "${GREEN}Build abgeschlossen!${NC}"
echo "Kompilierte Binärdateien finden Sie im Verzeichnis '$BUILD_DIR':"
echo "  - $BUILD_DIR/HashBackup-macos (macOS x64)"
echo "  - $BUILD_DIR/HashBackup-linux (Linux x64)"
