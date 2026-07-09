#!/usr/bin/env bash
set -euo pipefail

# Build HashBackup for the local macOS host and for the x64 Linux NAS deployment.
# All final binaries are deliberately placed in ./builds.

readonly SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
readonly BUILD_DIR="${SCRIPT_DIR}/builds"
readonly PROJECT_FILE="HashBackup/HashBackup.csproj"
readonly TARGET_FRAMEWORK="net10.0"
readonly CONTAINER_NAME="hashbackup-linux-build-$$"

cleanup() {
    docker rm -f "${CONTAINER_NAME}" >/dev/null 2>&1 || true
}
trap cleanup EXIT

case "$(uname -m)" in
    arm64) readonly MACOS_RID="osx-arm64" ;;
    x86_64) readonly MACOS_RID="osx-x64" ;;
    *) echo "Nicht unterstützte macOS-Architektur: $(uname -m)" >&2; exit 1 ;;
esac

cd "${SCRIPT_DIR}"
mkdir -p "${BUILD_DIR}"

printf 'HashBackup-Build (%s)\n' "${MACOS_RID}"
printf '%s\n' '------------------------------'

printf '%s\n' 'Kompiliere macOS-Version...'
dotnet publish "${PROJECT_FILE}" \
    -c Release \
    -r "${MACOS_RID}" \
    --self-contained true \
    /p:PublishAot=false \
    /p:PublishTrimmed=true \
    /p:PublishReadyToRun=true \
    /p:PublishSingleFile=true

cp -f "HashBackup/bin/Release/${TARGET_FRAMEWORK}/${MACOS_RID}/publish/HashBackup" \
    "${BUILD_DIR}/HashBackup-macos"

printf '%s\n' 'Kompiliere Linux-x64-NativeAOT-Version mit Docker...'
docker build --platform linux/amd64 -t hashbackup-linux-build .
docker create --platform linux/amd64 --name "${CONTAINER_NAME}" hashbackup-linux-build echo >/dev/null
docker cp "${CONTAINER_NAME}:/app/publish/HashBackup" "${BUILD_DIR}/HashBackup-linux"

chmod +x "${BUILD_DIR}/HashBackup-macos" "${BUILD_DIR}/HashBackup-linux"

printf '%s\n' 'Build abgeschlossen:'
printf '  %s (macOS %s)\n' "${BUILD_DIR}/HashBackup-macos" "${MACOS_RID}"
printf '  %s (Linux x64)\n' "${BUILD_DIR}/HashBackup-linux"
