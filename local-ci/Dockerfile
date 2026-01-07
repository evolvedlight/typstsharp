FROM ubuntu:24.04

ENV DEBIAN_FRONTEND=noninteractive

# Install dependencies matching the GitHub Action
RUN apt-get update && apt-get install -y --no-install-recommends \
    build-essential \
    pkg-config \
    libssl-dev \
    llvm \
    clang \
    zlib1g-dev \
    mingw-w64 \
    ca-certificates \
    musl-tools \
    curl \
    wget \
    git \
    libicu-dev \
    && rm -rf /var/lib/apt/lists/*

# Configure Musl Linker (matching action.yml)
RUN if [ ! -f /usr/local/bin/x86_64-linux-musl-gcc ]; then \
      ln -s $(which musl-gcc) /usr/local/bin/x86_64-linux-musl-gcc; \
    fi

# Install Rust
ENV RUSTUP_HOME=/usr/local/rustup \
    CARGO_HOME=/usr/local/cargo \
    PATH=/usr/local/cargo/bin:$PATH

RUN curl --proto '=https' --tlsv1.2 -sSf https://sh.rustup.rs | sh -s -- -y --default-toolchain 1.91.1 \
    && chmod -R a+w $RUSTUP_HOME $CARGO_HOME

# Add Rust targets
RUN rustup target add x86_64-pc-windows-gnu x86_64-unknown-linux-musl x86_64-unknown-linux-gnu

# Install .NET SDKs using the official install script
RUN curl -sSL https://dot.net/v1/dotnet-install.sh -o dotnet-install.sh \
    && chmod +x dotnet-install.sh \
    && ./dotnet-install.sh --channel 8.0 --install-dir /usr/share/dotnet \
    && ./dotnet-install.sh --channel 9.0 --install-dir /usr/share/dotnet \
    && ./dotnet-install.sh --channel 10.0 --install-dir /usr/share/dotnet || echo "dotnet 10 install failed, continuing" \
    && ln -s /usr/share/dotnet/dotnet /usr/bin/dotnet \
    && rm dotnet-install.sh

ENV DOTNET_ROOT=/usr/share/dotnet \
    PATH=$PATH:/usr/share/dotnet

WORKDIR /app
