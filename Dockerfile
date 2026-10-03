FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 8080

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
