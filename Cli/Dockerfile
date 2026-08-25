FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Cli/Cli.csproj Cli/
RUN dotnet restore Cli/Cli.csproj
COPY Cli/ Cli/
RUN dotnet publish Cli/Cli.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Cli.dll"]