FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Gateway/Gateway.csproj Gateway/
RUN dotnet restore Gateway/Gateway.csproj
COPY Gateway/ Gateway/
RUN dotnet publish Gateway/Gateway.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Gateway.dll"]
