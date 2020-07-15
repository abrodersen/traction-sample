FROM mcr.microsoft.com/dotnet/core/sdk:3.1-buster AS build

WORKDIR /src
COPY src/Server/Server.csproj .

RUN dotnet restore Server.csproj
COPY src/Server .
RUN dotnet publish Server.csproj -p:SkipWebpack=True -c Release -o /app/publish


FROM docker.io/library/node:14-buster as frontend
WORKDIR /src
COPY src/Server/ClientApp .

RUN npm install
RUN npm run build

FROM mcr.microsoft.com/dotnet/core/aspnet:3.1-buster-slim
WORKDIR /app
EXPOSE 80

COPY --from=build /app/publish .
COPY --from=frontend /src/build ./ClientApp/build

CMD ["dotnet", "Server.dll"]