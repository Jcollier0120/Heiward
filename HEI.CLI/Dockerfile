# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["HEI.Core/HEI.Core.csproj", "HEI.Core/"]
COPY ["HEI.CLI/HEI.CLI.csproj", "HEI.CLI/"]
RUN dotnet restore "HEI.CLI/HEI.CLI.csproj"
COPY HEI.Core/ HEI.Core/
COPY HEI.CLI/ HEI.CLI/
RUN dotnet publish "HEI.CLI/HEI.CLI.csproj" -c Release -o /app/publish --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["./hei-cli"]
