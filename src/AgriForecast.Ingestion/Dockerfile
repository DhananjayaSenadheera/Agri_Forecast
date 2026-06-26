# Build context MUST be the solution root (src/) so the Domain/Application/
# Infrastructure project references resolve.
#   docker build -f AgriForecast.Ingestion/Dockerfile -t agriforecast-ingestion:latest .
# (run from the src/ directory)

# ---- build stage ----
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy project files first for restore-layer caching.
COPY AgriForecast.Domain/AgriForecast.Domain.csproj AgriForecast.Domain/
COPY AgriForecast.Application/AgriForecast.Application.csproj AgriForecast.Application/
COPY AgriForecast.Infrastructure/AgriForecast.Infrastructure.csproj AgriForecast.Infrastructure/
COPY AgriForecast.Ingestion/AgriForecast.Ingestion.csproj AgriForecast.Ingestion/

RUN dotnet restore AgriForecast.Ingestion/AgriForecast.Ingestion.csproj

# Copy the rest of the source for the referenced projects.
COPY AgriForecast.Domain/ AgriForecast.Domain/
COPY AgriForecast.Application/ AgriForecast.Application/
COPY AgriForecast.Infrastructure/ AgriForecast.Infrastructure/
COPY AgriForecast.Ingestion/ AgriForecast.Ingestion/

RUN dotnet publish AgriForecast.Ingestion/AgriForecast.Ingestion.csproj \
    -c Release -o /app/publish --no-restore

# ---- runtime stage ----
# Worker/console host -> runtime image (NOT aspnet).
FROM mcr.microsoft.com/dotnet/runtime:9.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# The container's job is exactly one ingestion pass, then exit.
ENV Ingestion__RunOnce=true

# NOTE: no DB connection string baked in. appsettings.json keeps localhost as a
# placeholder; supply the real connection string at run time via
#   -e "ConnectionStrings__DefaultConnection=..."
ENTRYPOINT ["dotnet", "AgriForecast.Ingestion.dll"]
