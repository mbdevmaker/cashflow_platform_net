# Imagem multi-stage genérica para os serviços .NET.
#   docker build -f deploy/docker/Dockerfile --build-arg PROJECT=src/Services/CashFlow/CashFlow.Api/CashFlow.Api.csproj --build-arg APP_DLL=CashFlow.Api.dll .
ARG DOTNET_VERSION=10.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
ARG PROJECT
WORKDIR /src

# 1) Restore em camada própria (cache): apenas arquivos de projeto/configuração.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/BuildingBlocks/Contracts/*.csproj src/BuildingBlocks/Contracts/
COPY src/BuildingBlocks/Observability/*.csproj src/BuildingBlocks/Observability/
COPY src/BuildingBlocks/Security/*.csproj src/BuildingBlocks/Security/
COPY src/Services/CashFlow/CashFlow.Domain/*.csproj src/Services/CashFlow/CashFlow.Domain/
COPY src/Services/CashFlow/CashFlow.Application/*.csproj src/Services/CashFlow/CashFlow.Application/
COPY src/Services/CashFlow/CashFlow.Infrastructure/*.csproj src/Services/CashFlow/CashFlow.Infrastructure/
COPY src/Services/CashFlow/CashFlow.Api/*.csproj src/Services/CashFlow/CashFlow.Api/
COPY src/Services/DailyConsolidation/DailyConsolidation.Domain/*.csproj src/Services/DailyConsolidation/DailyConsolidation.Domain/
COPY src/Services/DailyConsolidation/DailyConsolidation.Application/*.csproj src/Services/DailyConsolidation/DailyConsolidation.Application/
COPY src/Services/DailyConsolidation/DailyConsolidation.Infrastructure/*.csproj src/Services/DailyConsolidation/DailyConsolidation.Infrastructure/
COPY src/Services/DailyConsolidation/DailyConsolidation.Api/*.csproj src/Services/DailyConsolidation/DailyConsolidation.Api/
RUN dotnet restore "$PROJECT"

# 2) Código e publish.
COPY src/ src/
RUN dotnet publish "$PROJECT" -c Release -o /app --no-restore -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS runtime
ARG APP_DLL
# curl para health check do container; tzdata garante fusos IANA (America/Sao_Paulo).
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl tzdata \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
ENV APP_DLL=${APP_DLL} \
    ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_RUNNING_IN_CONTAINER=true
EXPOSE 8080
# Usuário não-root fornecido pela imagem base (.NET 8+).
USER $APP_UID
HEALTHCHECK --interval=10s --timeout=3s --start-period=20s --retries=5 \
    CMD curl -fsS http://localhost:8080/health/live || exit 1
ENTRYPOINT ["sh", "-c", "exec dotnet \"$APP_DLL\" \"$@\"", "--"]
