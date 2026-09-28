# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["HEI.Core/HEI.Core.csproj", "HEI.Core/"]
COPY ["HEI.Web/HEI.Web.csproj", "HEI.Web/"]
RUN dotnet restore "HEI.Web/HEI.Web.csproj"
COPY HEI.Core/ HEI.Core/
COPY HEI.Web/ HEI.Web/
RUN dotnet publish "HEI.Web/HEI.Web.csproj" -c Release -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "HEI.Web.dll"]
