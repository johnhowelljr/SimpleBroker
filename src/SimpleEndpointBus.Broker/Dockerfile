FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/SimpleEndpointBus.Broker/SimpleEndpointBus.Broker.csproj -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /out ./

ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080/tcp
EXPOSE 37777/udp

ENTRYPOINT ["dotnet", "SimpleEndpointBus.Broker.dll"]
