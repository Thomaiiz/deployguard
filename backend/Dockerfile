# syntax=docker/dockerfile:1

# ---- Etapa 1: build (imagem grande, com SDK) ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copiar só os .csproj primeiro permite ao Docker reaproveitar o cache do
# "restore" enquanto as dependências não mudarem.
COPY DeployGuard.sln ./
COPY src/DeployGuard.Api/DeployGuard.Api.csproj src/DeployGuard.Api/
RUN dotnet restore src/DeployGuard.Api/DeployGuard.Api.csproj

COPY src/ src/
RUN dotnet publish src/DeployGuard.Api/DeployGuard.Api.csproj -c Release -o /app/publish --no-restore

# ---- Etapa 2: runtime (imagem mínima) ----
# "chiseled": Ubuntu reduzido sem shell nem gerenciador de pacotes, roda como
# usuário não-root por padrão. Menos binários = menos superfície de ataque.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "DeployGuard.Api.dll"]
