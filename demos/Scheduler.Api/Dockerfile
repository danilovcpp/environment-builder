FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Packages.props ./
COPY Directory.Build.props ./
COPY Shared/ Shared/
COPY Scheduler.Api/ Scheduler.Api/
RUN dotnet publish Scheduler.Api/Scheduler.Api.csproj -c Release -p:RestoreLockedMode=true -o /out
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out ./
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Scheduler.Api.dll"]
