using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Configuración
// ---------------------------------------------------------------------------
builder.Services.Configure<JwtSettings>(builder.Configuration.GetSection("Jwt"));
builder.Services.Configure<AuthSettings>(builder.Configuration.GetSection("Auth"));

var jwt = builder.Configuration.GetSection("Jwt").Get<JwtSettings>()
          ?? throw new InvalidOperationException("Falta la sección 'Jwt' en la configuración.");

if (Encoding.UTF8.GetByteCount(jwt.Key) < 32)
    throw new InvalidOperationException("Jwt:Key debe tener al menos 32 bytes (256 bits) para HS256.");

// ---------------------------------------------------------------------------
// Autenticación / Autorización JWT Bearer
// ---------------------------------------------------------------------------
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero
        };
    });

builder.Services.AddAuthorization();

// ---------------------------------------------------------------------------
// HttpClient tipado por nombre hacia el servicio downstream (jsonplaceholder)
// ---------------------------------------------------------------------------
builder.Services.AddHttpClient(Downstream.ClientName, (sp, client) =>
{
    var baseUrl = builder.Configuration["Downstream:BaseUrl"] ?? "https://jsonplaceholder.typicode.com/";
    client.BaseAddress = new Uri(baseUrl);
    client.Timeout = TimeSpan.FromSeconds(15);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Modulo5-ApiGateway/1.0");
});

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

// ---------------------------------------------------------------------------
// Endpoints
// ---------------------------------------------------------------------------
app.MapGet("/", () => Results.Ok(new
{
    servicio = "Modulo5 API Gateway",
    endpoints = new[] { "POST /api/login", "GET /api/users (Bearer)", "GET /api/posts (Bearer)" }
}));

// 1) Login: público, devuelve JWT si las credenciales son correctas.
app.MapPost("/api/login", (LoginRequest request, IOptions<AuthSettings> auth, IOptions<JwtSettings> jwtOptions) =>
{
    var cfg = auth.Value;
    if (request is null
        || string.IsNullOrWhiteSpace(request.Usuario)
        || string.IsNullOrWhiteSpace(request.Password)
        || request.Usuario != cfg.Usuario
        || request.Password != cfg.Password)
    {
        return Results.Unauthorized();
    }

    var settings = jwtOptions.Value;
    var expires = DateTime.UtcNow.AddMinutes(settings.ExpirationMinutes);

    var claims = new[]
    {
        new Claim(JwtRegisteredClaimNames.Sub, request.Usuario),
        new Claim(JwtRegisteredClaimNames.UniqueName, request.Usuario),
        new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
    };

    var credentials = new SigningCredentials(
        new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.Key)),
        SecurityAlgorithms.HmacSha256);

    var token = new JwtSecurityToken(
        issuer: settings.Issuer,
        audience: settings.Audience,
        claims: claims,
        notBefore: DateTime.UtcNow,
        expires: expires,
        signingCredentials: credentials);

    return Results.Ok(new LoginResponse(
        AccessToken: new JwtSecurityTokenHandler().WriteToken(token),
        TokenType: "Bearer",
        ExpiresIn: settings.ExpirationMinutes * 60,
        ExpiresAtUtc: expires));
})
.AllowAnonymous();

// 2) y 3) Proxy protegido hacia jsonplaceholder.
var api = app.MapGroup("/api").RequireAuthorization();

api.MapGet("/users", (IHttpClientFactory factory, CancellationToken ct) =>
    Downstream.ForwardAsync(factory, "users", ct));

api.MapGet("/posts", (IHttpClientFactory factory, CancellationToken ct) =>
    Downstream.ForwardAsync(factory, "posts", ct));

app.Run();

// ---------------------------------------------------------------------------
// Tipos auxiliares
// ---------------------------------------------------------------------------
public sealed class JwtSettings
{
    public string Key { get; set; } = string.Empty;
    public string Issuer { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public int ExpirationMinutes { get; set; } = 60;
}

public sealed class AuthSettings
{
    public string Usuario { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public sealed record LoginRequest(string Usuario, string Password);

public sealed record LoginResponse(string AccessToken, string TokenType, int ExpiresIn, DateTime ExpiresAtUtc);

public static class Downstream
{
    public const string ClientName = "jsonplaceholder";

    /// <summary>
    /// Llama al servicio downstream y devuelve al cliente el mismo cuerpo, Content-Type y status code.
    /// Si el downstream no responde, devuelve 502 Bad Gateway (o 504 si hubo timeout).
    /// </summary>
    public static async Task<IResult> ForwardAsync(IHttpClientFactory factory, string path, CancellationToken ct)
    {
        var client = factory.CreateClient(ClientName);
        try
        {
            using var response = await client.GetAsync(path, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/json; charset=utf-8";

            return Results.Content(body, contentType, statusCode: (int)response.StatusCode);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return Results.Problem("El servicio downstream no respondió a tiempo.", statusCode: StatusCodes.Status504GatewayTimeout);
        }
        catch (HttpRequestException ex)
        {
            return Results.Problem($"No se pudo contactar al servicio downstream: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
        }
    }
}
