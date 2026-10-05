FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 8080

# Shared libraries headless Chrome needs. The live scraper's PuppeteerSharp downloads Chrome
# itself on first run (BrowserFetcher), but the slim aspnet image ships none of its system
# dependencies, so without these the browser fails to launch (e.g. missing libgobject-2.0).
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ca-certificates fonts-liberation \
        libasound2 libatk-bridge2.0-0 libatk1.0-0 libatspi2.0-0 libcairo2 libcups2 \
        libdbus-1-3 libdrm2 libexpat1 libgbm1 libglib2.0-0 libnspr4 libnss3 \
        libpango-1.0-0 libx11-6 libxcb1 libxcomposite1 libxdamage1 libxext6 \
        libxfixes3 libxkbcommon0 libxrandr2 \
    && rm -rf /var/lib/apt/lists/*

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["src/OctopusPrediction.Api/OctopusPrediction.Api.csproj", "src/OctopusPrediction.Api/"]
RUN dotnet restore "src/OctopusPrediction.Api/OctopusPrediction.Api.csproj"
COPY . .
WORKDIR "/src/src/OctopusPrediction.Api"
RUN dotnet build "OctopusPrediction.Api.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "OctopusPrediction.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "OctopusPrediction.Api.dll"]
