# syntax=docker/dockerfile:1
# Build context must be the repo root (central package management lives there):
#   docker build -f src/Services/Marketing/Api/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
# Release identity (ADR-0064): GIT_SHA (an env var here) -> SourceRevisionId via Directory.Build.props and
# VERSION -> -p:Version, so the InformationalVersion the service reads at startup is <VERSION>+<GIT_SHA>.
ARG GIT_SHA=""
ARG VERSION=""
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked dotnet publish src/Services/Marketing/Api/3commerce.Marketing.Api.csproj -c Release -o /app ${VERSION:+-p:Version=$VERSION}

# Continuous profiling (ADR-0068): the Pyroscope .NET profiler — a native CLR profiler, Linux only (x86_64 + aarch64,
# glibc). Pinned release + SHA-256 per architecture; debug info stripped (185 MB -> ~16 MB). This stage is identical in
# every .NET Dockerfile and its output is copied right above the app, so the layer is shared by all images. The image
# only CARRIES the profiler: it loads only when the deployment sets CORECLR_ENABLE_PROFILING=1 + LD_PRELOAD (Helm
# profiling.enabled, deploy/.env.dev).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS profiler
ARG TARGETARCH
RUN set -eu; \
    case "${TARGETARCH:-amd64}" in \
      arm64) arch=aarch64; sum=de0f1fc384f2af754bfb331ce4c0028ba92ec6a25ce98efba4aa9e2f81a012c4 ;; \
      *)     arch=x86_64;  sum=9efda5cf621511ff937a0f765fe8af0ea59ab8c47a227c8afcf25637779dd36f ;; \
    esac; \
    apt-get update && apt-get install -y --no-install-recommends binutils && rm -rf /var/lib/apt/lists/*; \
    curl -fsSL --retry 6 --retry-all-errors --retry-delay 5 --http1.1 -o /tmp/pyroscope.tgz \
      "https://github.com/grafana/pyroscope-dotnet/releases/download/pyroscope-1.6.1/pyroscope.1.6.1-glibc-${arch}.tar.gz"; \
    echo "${sum}  /tmp/pyroscope.tgz" | sha256sum -c -; \
    mkdir -p /pyroscope && tar xzf /tmp/pyroscope.tgz -C /pyroscope && rm /tmp/pyroscope.tgz; \
    strip --strip-debug /pyroscope/Pyroscope.Profiler.Native.so

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
COPY --from=profiler /pyroscope /pyroscope
ENV CORECLR_PROFILER={BD1A650D-AC5D-4896-B64F-D6FA25D6B26A} \
    CORECLR_PROFILER_PATH=/pyroscope/Pyroscope.Profiler.Native.so \
    PYROSCOPE_PROFILING_LOG_DIR=/tmp/pyroscope
ENV ASPNETCORE_URLS=http://+:8080
WORKDIR /app
COPY --from=build /app .
# OCI image labels (ADR-0064): which commit this image is. Last, so a new SHA never busts the layers above.
ARG GIT_SHA=""
ARG VERSION=""
LABEL org.opencontainers.image.source="https://github.com/lal3in/3commerce" \
      org.opencontainers.image.revision="$GIT_SHA" \
      org.opencontainers.image.version="$VERSION"
# The profiler's application name = the OpenTelemetry service.name, so Tempo spans link to these profiles.
ENV PYROSCOPE_APPLICATION_NAME=marketing
ENTRYPOINT ["dotnet", "3commerce.Marketing.Api.dll"]
