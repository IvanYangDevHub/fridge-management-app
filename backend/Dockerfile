FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/FreshDays/FreshDays.csproj src/FreshDays/
COPY src/FreshDays.Migration/FreshDays.Migration.csproj src/FreshDays.Migration/
RUN dotnet restore src/FreshDays.Migration/FreshDays.Migration.csproj
COPY src/FreshDays/ src/FreshDays/
COPY src/FreshDays.Migration/ src/FreshDays.Migration/
RUN dotnet publish src/FreshDays.Migration/FreshDays.Migration.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "FreshDays.dll"]
