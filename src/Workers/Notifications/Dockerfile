# syntax=docker/dockerfile:1
# Build context must be the repo root (central package management lives there):
#   docker build -f src/Workers/Notifications/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN --mount=type=cache,target=/root/.nuget/packages dotnet publish src/Workers/Notifications/3commerce.Workers.Notifications.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "3commerce.Workers.Notifications.dll"]
