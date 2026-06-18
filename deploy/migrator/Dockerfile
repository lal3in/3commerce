# One-shot schema migrator: builds a framework-dependent EF migration bundle per
# DB-owning service and applies them once. Runs AFTER Postgres init SQL (which creates
# the service databases/roles/extensions) and BEFORE the app services start.
#
# Framework-dependent (no -r/--self-contained) on a multi-arch dotnet/runtime base, so the
# same Dockerfile works on arm64 (local colima) and amd64 (CI) — the SDK and runtime images
# both resolve to the build host's architecture.
#
# Build context must be the repo root:  docker build -f deploy/migrator/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore 3commerce.sln
RUN dotnet tool install -g dotnet-ef --version 10.0.9
ENV PATH="$PATH:/root/.dotnet/tools"
RUN mkdir -p /bundles && for s in Identity Catalog Entity Ordering Payments Fulfillment Support; do \
      l=$(echo "$s" | tr '[:upper:]' '[:lower:]'); \
      echo "== bundling $s =="; \
      dotnet ef migrations bundle \
        --project "src/Services/$s/Infrastructure" \
        --startup-project "src/Services/$s/Api" \
        --configuration Release \
        --output "/bundles/efbundle-$l"; \
    done

# aspnet base (not runtime): the bundles are framework-dependent on the ASP.NET shared
# framework because each startup project is a web app.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /bundles
COPY --from=build /bundles/ ./
COPY deploy/migrator/entrypoint.sh /usr/local/bin/entrypoint.sh
RUN chmod +x /usr/local/bin/entrypoint.sh
ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
