FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 8080

# Shared libraries headless Chrome needs. The slim aspnet image ships none of them, so without
# these the browser fails to launch (e.g. missing libgobject-2.0).
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ca-certificates fonts-liberation \
        libasound2 libatk-bridge2.0-0 libatk1.0-0 libatspi2.0-0 libcairo2 libcups2 \
        libdbus-1-3 libdrm2 libexpat1 libgbm1 libglib2.0-0 libnspr4 libnss3 \
        libpango-1.0-0 libx11-6 libxcb1 libxcomposite1 libxdamage1 libxext6 \
        libxfixes3 libxkbcommon0 libxrandr2 \
    && rm -rf /var/lib/apt/lists/*

# The live scraper's Chrome, installed at build time. Render wipes the disk whenever the free
# service sleeps or restarts, so a browser downloaded at runtime (BrowserFetcher) was fetched
# again on every wake. The scraper still downloads one if this path is ever missing.
# Same version PuppeteerSharp 20 downloads; bump both together.
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS chrome
ARG CHROME_VERSION=128.0.6613.119
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl unzip ca-certificates \
    && curl -fsSL -o /tmp/chrome.zip \
        "https://storage.googleapis.com/chrome-for-testing-public/${CHROME_VERSION}/linux64/chrome-headless-shell-linux64.zip" \
    && unzip -q /tmp/chrome.zip -d /opt \
    && mv /opt/chrome-headless-shell-linux64 /opt/chrome-headless-shell \
    && rm /tmp/chrome.zip

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
COPY --from=chrome /opt/chrome-headless-shell /opt/chrome-headless-shell
ENV LiveScoreScraper__ChromeExecutablePath=/opt/chrome-headless-shell/chrome-headless-shell
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "OctopusPrediction.Api.dll"]
