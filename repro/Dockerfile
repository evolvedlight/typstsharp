FROM ubuntu:22.04

# Install .NET 8
RUN apt-get update && apt-get install -y wget ca-certificates \
    && wget https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb -O packages-microsoft-prod.deb \
    && dpkg -i packages-microsoft-prod.deb \
    && rm packages-microsoft-prod.deb \
    && apt-get update \
    && apt-get install -y dotnet-sdk-8.0

WORKDIR /app

# Copy artifacts
COPY artifacts /app/artifacts

# Copy TestApp
COPY repro/TestApp /app/TestApp

WORKDIR /app/TestApp

# Restore and build
RUN dotnet restore
RUN dotnet build -c Release

# Run
CMD ["dotnet", "run", "-c", "Release"]