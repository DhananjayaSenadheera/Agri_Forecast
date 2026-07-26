# AgriForecast API (.NET 9 Clean Architecture — ASP.NET Core host).
#
# Build context MUST be the solution root (src/) so the Domain/Application/
# Infrastructure project references resolve.
#   docker build -f AgriForecast.API/Dockerfile -t agriforecast-api:latest .
# (run from the src/ directory), or from the repo root:
#   docker build -f src/AgriForecast.API/Dockerfile -t agriforecast-api:latest src/
#
# NO secrets baked in. appsettings.json intentionally ships empty values for
# ConnectionStrings:DefaultConnection, Jwt:Key and MlService:AdminApiKey; the
# app FAILS FAST at startup if Jwt:Key is missing. Supply all of them at run
# time via environment variables (in Kubernetes: from the agri-db / agri-jwt /
# agri-ml-key secrets — see k8s/forecast-api.yaml).

# ---- build stage ----
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy project files first for restore-layer caching.
COPY AgriForecast.Domain/AgriForecast.Domain.csproj AgriForecast.Domain/
COPY AgriForecast.Application/AgriForecast.Application.csproj AgriForecast.Application/
COPY AgriForecast.Infrastructure/AgriForecast.Infrastructure.csproj AgriForecast.Infrastructure/
COPY AgriForecast.API/AgriForecast.API.csproj AgriForecast.API/

RUN dotnet restore AgriForecast.API/AgriForecast.API.csproj

# Copy the rest of the source for the referenced projects.
COPY AgriForecast.Domain/ AgriForecast.Domain/
COPY AgriForecast.Application/ AgriForecast.Application/
COPY AgriForecast.Infrastructure/ AgriForecast.Infrastructure/
COPY AgriForecast.API/ AgriForecast.API/

RUN dotnet publish AgriForecast.API/AgriForecast.API.csproj \
    -c Release -o /app/publish --no-restore

# ---- runtime stage ----
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# aspnet:9.0 listens on 8080 by default (ASPNETCORE_HTTP_PORTS=8080), which is
# non-privileged, so the container can run as the image's built-in non-root
# 'app' user. HTTPS is terminated outside the container (there is no https
# port configured, so UseHttpsRedirection is a no-op in-container).
EXPOSE 8080
USER $APP_UID

ENTRYPOINT ["dotnet", "AgriForecast.API.dll"]
