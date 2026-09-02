FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Worker/Worker.csproj Worker/
COPY Shared/Shared.csproj Shared/
RUN dotnet restore Worker/Worker.csproj
COPY Worker/ Worker/
COPY Shared/ Shared/
RUN dotnet publish Worker/Worker.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Workflow.Worker.dll"]