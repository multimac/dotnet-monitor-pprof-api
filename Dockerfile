FROM mcr.microsoft.com/dotnet/aspnet:7.0 AS base

WORKDIR /app
EXPOSE 5063

ENV ASPNETCORE_URLS=http://+:5063

# Creates a non-root user with an explicit UID and adds permission to access the /app folder
# For more info, please refer to https://aka.ms/vscode-docker-dotnet-configure-containers
RUN adduser -u 5678 --disabled-password --gecos "" pprof-api && chown -R pprof-api /app
USER pprof-api

FROM mcr.microsoft.com/dotnet/sdk:7.0 AS build
WORKDIR /src

COPY ["DotNet.Monitor.PProf.Api.csproj", "./"]
RUN dotnet restore "DotNet.Monitor.PProf.Api.csproj"

COPY . .
RUN dotnet build "DotNet.Monitor.PProf.Api.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "DotNet.Monitor.PProf.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app

COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "DotNet.Monitor.PProf.Api.dll"]
