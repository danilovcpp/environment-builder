FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Packages.props ./
COPY Directory.Build.props ./
COPY Shared/ Shared/
COPY Reporting.Api/ Reporting.Api/
RUN dotnet publish Reporting.Api/Reporting.Api.csproj -c Release -p:RestoreLockedMode=true -o /out
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out ./
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Reporting.Api.dll"]
