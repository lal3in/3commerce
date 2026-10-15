# syntax=docker/dockerfile:1
# Build context must be the repo root (central package management lives there):
#   docker build -f src/SupplierPortal/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
# Release identity (ADR-0064): GIT_SHA (an env var here) -> SourceRevisionId via Directory.Build.props and
# VERSION -> -p:Version, so the InformationalVersion the service reads at startup is <VERSION>+<GIT_SHA>.
ARG GIT_SHA=""
ARG VERSION=""
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked dotnet publish src/SupplierPortal/3commerce.SupplierPortal.csproj -c Release -o /app ${VERSION:+-p:Version=$VERSION}

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
ENV ASPNETCORE_URLS=http://+:8080
WORKDIR /app
COPY --from=build /app .
# OCI image labels (ADR-0064): which commit this image is. Last, so a new SHA never busts the layers above.
ARG GIT_SHA=""
ARG VERSION=""
LABEL org.opencontainers.image.source="https://github.com/lal3in/3commerce" \
      org.opencontainers.image.revision="$GIT_SHA" \
      org.opencontainers.image.version="$VERSION"
ENTRYPOINT ["dotnet", "3commerce.SupplierPortal.dll"]
