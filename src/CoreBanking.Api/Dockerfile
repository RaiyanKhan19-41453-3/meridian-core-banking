# ---------- build stage ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# restore first (better layer caching)
COPY CoreBanking.Api.csproj ./
RUN dotnet restore

COPY . ./
RUN dotnet publish -c Release -o /out --no-restore

# ---------- runtime stage ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080

# curl is needed for the Docker healthcheck
USER root
RUN apt-get update && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
USER $APP_UID

EXPOSE 8080

COPY --from=build /out ./
ENTRYPOINT ["dotnet", "CoreBanking.Api.dll"]
