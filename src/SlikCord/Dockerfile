FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
EXPOSE 3100
ENV ASPNETCORE_HTTP_PORTS=3100

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /sln
COPY . .
RUN dotnet publish "/sln/src/SlikCord/SlikCord.csproj" -c Release -o /app/publish --framework net10.0

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "SlikCord.dll"]