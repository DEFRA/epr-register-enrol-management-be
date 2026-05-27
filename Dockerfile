# Build stage image
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY EprRegisterEnrolManagementBe.sln ./
COPY EprRegisterEnrolManagementBe/EprRegisterEnrolManagementBe.csproj EprRegisterEnrolManagementBe/
COPY EprRegisterEnrolManagementBe.Test/EprRegisterEnrolManagementBe.Test.csproj EprRegisterEnrolManagementBe.Test/
RUN dotnet restore EprRegisterEnrolManagementBe.sln
COPY . .
RUN dotnet publish EprRegisterEnrolManagementBe -c Release -o /app/publish /p:UseAppHost=false

# Development image: runs `dotnet watch` for hot reload during local development.
# Used by docker compose --watch via the `develop:` block in compose.yml.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS development
WORKDIR /src
COPY . .
WORKDIR /src/EprRegisterEnrolManagementBe
RUN dotnet restore
EXPOSE 8085
ENV DOTNET_USE_POLLING_FILE_WATCHER=1
ENV ASPNETCORE_URLS=http://+:8085
ENTRYPOINT ["dotnet", "watch", "run", "--no-launch-profile", "--non-interactive"]

# Final production image
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

# Add curl to template, CDP PLATFORM HEALTHCHECK REQUIREMENT
RUN apt update && \
    apt install curl -y && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .
RUN useradd -r -u 1001 -g root dotnetuser && \
    mkdir -p /home/dotnetuser && \
    chown 1001:0 /home/dotnetuser && \
    chmod g=u /home/dotnetuser && \
    chown -R 1001:0 /app
ENV HOME=/home/dotnetuser
USER 1001
EXPOSE 8085
HEALTHCHECK --interval=10s --timeout=5s --start-period=30s --retries=3 \
    CMD curl -fsS http://localhost:8085/health || exit 1
ENTRYPOINT ["dotnet", "EprRegisterEnrolManagementBe.dll"]
