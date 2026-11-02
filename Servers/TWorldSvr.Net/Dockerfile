# Multi-stage build: restore + publish with the .NET 10 SDK, run on the slim ASP.NET runtime.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY FourDev.Mail/FourDev.Mail.csproj FourDev.Mail/
COPY TWorldSvr.Net/TWorld.Protocol/TWorld.Protocol.csproj TWorldSvr.Net/TWorld.Protocol/
COPY TWorldSvr.Net/TWorld.Data/TWorld.Data.csproj TWorldSvr.Net/TWorld.Data/
COPY TWorldSvr.Net/TWorld.Server/TWorld.Server.csproj TWorldSvr.Net/TWorld.Server/
COPY TMapSvr.Net/TMap.Protocol/TMap.Protocol.csproj TMapSvr.Net/TMap.Protocol/
COPY TMapSvr.Net/TMap.Data/TMap.Data.csproj TMapSvr.Net/TMap.Data/
RUN dotnet restore TWorldSvr.Net/TWorld.Server/TWorld.Server.csproj

COPY FourDev.Mail/ FourDev.Mail/
COPY TWorldSvr.Net/TWorld.Protocol/ TWorldSvr.Net/TWorld.Protocol/
COPY TWorldSvr.Net/TWorld.Data/ TWorldSvr.Net/TWorld.Data/
COPY TWorldSvr.Net/TWorld.Server/ TWorldSvr.Net/TWorld.Server/
COPY TMapSvr.Net/TMap.Protocol/ TMapSvr.Net/TMap.Protocol/
COPY TMapSvr.Net/TMap.Data/ TMapSvr.Net/TMap.Data/
RUN dotnet publish TWorldSvr.Net/TWorld.Server/TWorld.Server.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
EXPOSE 3815
ENTRYPOINT ["dotnet", "TWorld.Server.dll"]
