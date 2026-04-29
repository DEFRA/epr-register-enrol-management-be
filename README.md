# EPR Register Case Management Backend (PoC)

A proof-of-concept .NET 10 backend API for the EPR Register case management
service. Built from
[cdp-dotnet-backend-template](https://github.com/DEFRA/cdp-dotnet-backend-template).

The backend exposes a JSON HTTP API and persists data in MongoDB. It is
designed to run alongside the
[`epr-register-case-management-frontend-poc`](../epr-register-case-management-frontend-poc/)
service.

- [Requirements](#requirements)
- [Local development](#local-development)
- [Running with Docker Compose](#running-with-docker-compose)
- [Endpoints](#endpoints)
- [Authentication](#authentication)
- [Testing](#testing)
- [Frontend integration](#frontend-integration)
- [Licence](#licence)

## Requirements

- [.NET SDK 10.0](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Docker](https://www.docker.com/) and Docker Compose (for the Docker workflow)
- [MongoDB 7+](https://www.mongodb.com/docs/manual/installation/) running
  locally on `mongodb://127.0.0.1:27017` (or use the Docker Compose stack)

## Local development

Restore and run the API directly with `dotnet`:

```bash
dotnet restore
dotnet run --project EprRegisterEnrolManagementBe --launch-profile EprRegisterEnrolManagementBe
```

The API listens on `http://localhost:8085`. Verify it is up:

```bash
curl http://localhost:8085/health
```

The MongoDB connection is configured in
[`EprRegisterEnrolManagementBe/appsettings.Development.json`](EprRegisterEnrolManagementBe/appsettings.Development.json)
and can be overridden via the `Mongo__DatabaseUri` and `Mongo__DatabaseName`
environment variables.

If you do not have MongoDB installed locally, start just the database from
the Compose stack:

```bash
docker compose up -d mongodb
```

## Running with Docker Compose

The repository ships a Compose stack that builds the API image and starts
its dependencies (MongoDB and a Floci-based AWS emulator):

```bash
docker compose up --build -d
```

Once the stack is healthy the API is reachable on `http://localhost:8085`.
Tear it down with:

```bash
docker compose down -v
```

## Endpoints

| Method | Path                                         | Description                            |
| ------ | -------------------------------------------- | -------------------------------------- |
| GET    | `/health`                                    | Health probe used by CDP               |
| POST   | `/work-items`                                | Submit a new work item                 |
| GET    | `/work-items`                                | List/search work items                 |
| GET    | `/work-items/{id}`                           | Get a single work item by id           |
| POST   | `/work-items/{id}/tasks/{taskId}/complete`   | Complete a task on a work item         |
| POST   | `/work-items/{id}/actions/{actionId}`        | Apply an action / state transition     |
| POST   | `/work-items/{id}/assign`                    | Assign a work item to a user           |
| POST   | `/work-items/{id}/unassign`                  | Unassign a work item                   |
| POST   | `/work-items/{id}/notes`                     | Add a note to a work item              |

## Authentication

All non-health endpoints require a CDP Cognito client ID supplied in the
`x-cdp-cognito-client-id` request header. CDP validates the upstream
service's JWT before forwarding the call, so the backend trusts the header's
presence and performs no further authorisation:

```bash
curl -H 'x-cdp-cognito-client-id: my-upstream-service' \
  http://localhost:8085/work-items
```

Requests without the header receive `401 Unauthorized`. The `/health`
endpoint is anonymous and remains reachable without authentication.

## Testing

Tests use [Ephemeral MongoDB](https://github.com/asimmon/ephemeral-mongo)
so they run end-to-end against a real (in-memory) Mongo instance:

```bash
dotnet test
```

## Frontend integration

The companion frontend
([`epr-register-case-management-frontend-poc`](../epr-register-case-management-frontend-poc/))
calls this API server-to-server. With both services running locally the
frontend at `http://localhost:3000/backend-status` reports the backend's
`/health` response, providing an end-to-end smoke test.

To run both services together via Docker Compose, see the
[frontend README](../epr-register-case-management-frontend-poc/README.md#running-the-full-stack).

## Deployment

This service targets the CDP platform. See
[`docs/cdp-deployment.md`](docs/cdp-deployment.md) for the container port,
required environment variables, secrets, AWS resources and Squid proxy
allow-list. Tracing behaviour is documented in
[`docs/cdp-tracing.md`](docs/cdp-tracing.md). Architecture decisions live
under [`docs/adr/`](docs/adr/).

## Licence

THIS INFORMATION IS LICENSED UNDER THE CONDITIONS OF THE OPEN GOVERNMENT
LICENCE found at: <http://www.nationalarchives.gov.uk/doc/open-government-licence/version/3>.
