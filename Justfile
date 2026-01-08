default:
    @just --list

# Build the project
build:
    dotnet build

# Run the bot locally
run:
    dotnet run --project src/HomelabBot

# Watch mode for development
watch:
    dotnet watch --project src/HomelabBot

# Run tests
test:
    dotnet test

# Check code formatting
lint:
    dotnet format --verify-no-changes

# Fix code formatting
format:
    dotnet format

# Build Docker image locally
docker-build:
    docker build -t homelab-bot .

# Run with Docker Compose
docker-run:
    docker compose up -d

# Stop Docker Compose
docker-stop:
    docker compose down

# View logs
docker-logs:
    docker compose logs -f

# Clean build artifacts
clean:
    dotnet clean
    rm -rf src/HomelabBot/bin src/HomelabBot/obj
