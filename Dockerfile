# Közös Rajzoló – többlépcsős build (.NET 10, arm64 és amd64 alatt is fut)
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY kozos_rajzolo/KozosRajzolo.csproj kozos_rajzolo/
RUN dotnet restore kozos_rajzolo/KozosRajzolo.csproj
COPY kozos_rajzolo/ kozos_rajzolo/
# A publish újra restore-ol, mert a Blazor keretrendszer-fájljait (blazor.web.js)
# csak akkor hozza be, ha már látja a .razor fájlokat.
RUN dotnet publish kozos_rajzolo/KozosRajzolo.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "KozosRajzolo.dll"]
