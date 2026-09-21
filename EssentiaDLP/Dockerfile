# See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER root
RUN apt-get update \
    && apt-get install -y --no-install-recommends python3 python3-pip python3-venv ffmpeg nodejs \
    && rm -rf /var/lib/apt/lists/*
ENV ASPNETCORE_HTTP_PORTS=8080
ENV DataDirectory=/data
ENV Python=python3
ENV AnalyzerScript=/app/analyzer/tag_library.py
ENV ModelsDirectory=/app/analyzer/models
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["EssentiaDLP/EssentiaDLP.csproj", "EssentiaDLP/"]
RUN dotnet restore "./EssentiaDLP/EssentiaDLP.csproj"
COPY . .
WORKDIR "/src/EssentiaDLP"
RUN dotnet publish "./EssentiaDLP.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
COPY EssentiaDLP/analyzer /app/analyzer
RUN python3 -m pip install --break-system-packages --no-cache-dir -r /app/analyzer/requirements.txt \
    && mkdir -p /data \
    && chown -R $APP_UID /app /data
USER $APP_UID
VOLUME ["/data"]
ENTRYPOINT ["dotnet", "EssentiaDLP.dll"]
