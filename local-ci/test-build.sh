#!/bin/bash
set -e

# Directory of this script
DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
PROJECT_ROOT="$(dirname "$DIR")"

echo "Building Docker image for local CI testing..."
docker build -t typstsharp-local-ci "$DIR"

echo "Running build inside container..."
# Mount the project root to /app
# Run the pack command
docker run --rm \
    -v "$PROJECT_ROOT:/app" \
    -v "cargo-cache:/usr/local/cargo/registry" \
    -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    -e DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
    typstsharp-local-ci \
    /bin/bash -c "
        git config --global --add safe.directory /app && \
        cd src/typst_core && cargo clean && cd ../.. && \
        dotnet restore typstsharp.slnx && \
        dotnet pack src/typstsharp/typstsharp.csproj -c Release -o artifacts /p:Version=0.0.3-local-test
    "

echo "Build complete! Artifacts are in the 'artifacts' directory."

