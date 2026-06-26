# syntax=docker/dockerfile:1
# Build context must be the repo root (central package management lives there):
#   docker build -f src/Gateway/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN --mount=type=cache,target=/root/.nuget/packages dotnet publish src/Gateway/3commerce.Gateway.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
ENV ASPNETCORE_URLS=http://+:8080
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "3commerce.Gateway.dll"]
