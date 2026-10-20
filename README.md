# Módulo 5 – Actividad 1: API Gateway

API Gateway sencillo construido con **ASP.NET Core Minimal API (.NET 8)**. Expone un endpoint de login que emite
**JWT Bearer Tokens** y dos endpoints protegidos que hacen de **proxy** hacia
[JSONPlaceholder](https://jsonplaceholder.typicode.com).

```
Cliente ──► API Gateway (:8080) ──► https://jsonplaceholder.typicode.com
            ├─ POST /api/login   (público, emite JWT)
            ├─ GET  /api/users   (requiere Bearer) ──► /users
            └─ GET  /api/posts   (requiere Bearer) ──► /posts
```

El cliente **nunca** llama directamente a JSONPlaceholder: el Gateway valida el token, hace la llamada
server-side con `HttpClient` (vía `IHttpClientFactory`) y devuelve al cliente el mismo JSON y el mismo status code.

## Endpoints

| Método | Ruta         | Auth          | Descripción                                                        |
|--------|--------------|---------------|--------------------------------------------------------------------|
| POST   | `/api/login` | No            | Valida credenciales y devuelve un JWT (expira en 60 min).          |
| GET    | `/api/users` | Bearer token  | Proxy a `https://jsonplaceholder.typicode.com/users`.              |
| GET    | `/api/posts` | Bearer token  | Proxy a `https://jsonplaceholder.typicode.com/posts`.              |

Respuestas relevantes:

- `200` — login correcto / respuesta del servicio downstream (se reenvía su status code tal cual).
- `401` — credenciales incorrectas en login, o token ausente / inválido / expirado en `/api/users` y `/api/posts`.
- `502` / `504` — el Gateway no pudo contactar a JSONPlaceholder o este no respondió a tiempo.

## Credenciales estáticas de login

Definidas en `appsettings.json` (sección `Auth`):

| Campo      | Valor          |
|------------|----------------|
| `usuario`  | `admin`        |
| `password` | `Gateway2026!` |

Body esperado por `POST /api/login`:

```json
{ "usuario": "admin", "password": "Gateway2026!" }
```

> Pueden sobrescribirse con variables de entorno (`Auth__Usuario`, `Auth__Password`), por ejemplo en
> `docker-compose.yml`. La clave de firma del JWT (`Jwt:Key`) es de ejemplo y solo apta para desarrollo.

## Cómo correrlo con Docker Compose

Requisitos: Docker Desktop (o Docker Engine con el plugin Compose).

```bash
docker compose up --build
```

El Gateway queda disponible en **http://localhost:8080**. Para detenerlo:

```bash
docker compose down
```

## Ejemplos con curl

### 1. Login (obtener token)

```bash
curl -s -X POST http://localhost:8080/api/login \
  -H "Content-Type: application/json" \
  -d '{"usuario":"admin","password":"Gateway2026!"}'
```

Respuesta:

```json
{
  "accessToken": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...",
  "tokenType": "Bearer",
  "expiresIn": 3600,
  "expiresAtUtc": "2026-10-19T18:00:00Z"
}
```

Guardar el token en una variable (bash, requiere `jq`):

```bash
TOKEN=$(curl -s -X POST http://localhost:8080/api/login \
  -H "Content-Type: application/json" \
  -d '{"usuario":"admin","password":"Gateway2026!"}' | jq -r .accessToken)
```

### 2. Usuarios (vía Gateway)

```bash
curl -i http://localhost:8080/api/users -H "Authorization: Bearer $TOKEN"
```

### 3. Posts (vía Gateway)

```bash
curl -i http://localhost:8080/api/posts -H "Authorization: Bearer $TOKEN"
```

### Casos de error

```bash
# Credenciales incorrectas -> 401
curl -i -X POST http://localhost:8080/api/login \
  -H "Content-Type: application/json" \
  -d '{"usuario":"admin","password":"incorrecta"}'

# Sin token -> 401
curl -i http://localhost:8080/api/users

# Token inválido -> 401
curl -i http://localhost:8080/api/posts -H "Authorization: Bearer token-invalido"
```

### Equivalente en PowerShell

```powershell
$login = Invoke-RestMethod -Method Post -Uri http://localhost:8080/api/login `
  -ContentType "application/json" -Body '{"usuario":"admin","password":"Gateway2026!"}'
$headers = @{ Authorization = "Bearer $($login.accessToken)" }
Invoke-RestMethod http://localhost:8080/api/users -Headers $headers
Invoke-RestMethod http://localhost:8080/api/posts -Headers $headers
```

## Correr sin Docker (desarrollo)

```bash
dotnet restore
dotnet run --launch-profile http
```

Queda escuchando en **http://localhost:5080** (mismos endpoints).

## Nota sobre la interpretación de "redirección"

El enunciado pide que el Gateway "redireccione" las peticiones a JSONPlaceholder. Esto se interpretó como
**proxy / forwarding server-side** y **no** como una redirección HTTP (`302 Found` / `307`):

- Con un `302`, el Gateway solo le diría al cliente "ve tú a JSONPlaceholder", y el cliente terminaría llamando
  directamente al servicio interno: se pierde el control centralizado (autenticación, logging, rate limiting) y
  se expone la dirección del servicio downstream.
- En un API Gateway real (Ocelot, YARP, Kong, NGINX, Azure API Management, etc.) el patrón estándar es que el
  propio Gateway reenvíe la petición al microservicio y devuelva la respuesta, actuando como **único punto de
  entrada**. Eso es lo que implementa este proyecto: el cliente solo conoce `http://localhost:8080`.

## Estructura

```
Modulo5_ApiGateway/
├── ApiGateway.csproj
├── Program.cs                  # Configuración JWT, HttpClientFactory y endpoints
├── appsettings.json            # Credenciales estáticas, configuración JWT y URL downstream
├── appsettings.Development.json
├── Properties/launchSettings.json
├── Dockerfile                  # Multi-stage: sdk:8.0 -> aspnet:8.0
├── docker-compose.yml          # Mapea 8080:8080
├── .dockerignore
└── .gitignore
```
