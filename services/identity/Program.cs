var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok", svc = app.Environment.ApplicationName }));

if (app.Environment.ApplicationName?.Contains("identity", StringComparison.OrdinalIgnoreCase) == true)
{
    app.MapPost("/token/dev", async (HttpRequest req, IConfiguration cfg) =>
    {
        using var sr = new StreamReader(req.Body);
        var body = await sr.ReadToEndAsync();
        var dto = System.Text.Json.JsonSerializer.Deserialize<DevTokenReq>(body)!;

        var key = cfg["JwtSigningKey"] ?? "dev_hmac_super_secret_change_me";
        var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
        var credentials = new Microsoft.IdentityModel.Tokens.SigningCredentials(
            new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(key)),
            Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256
        );
        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
            issuer: "hireflow-dev",
            audience: "hireflow-dev",
            claims: new[]
            {
                new System.Security.Claims.Claim("email", dto.email),
                new System.Security.Claims.Claim("tenant_id", dto.tenantId ?? Guid.Empty.ToString()),
                new System.Security.Claims.Claim("role", dto.role ?? "Recruiter")
            },
            expires: DateTime.UtcNow.AddHours(24),
            signingCredentials: credentials
        );
        return Results.Ok(new { access_token = handler.WriteToken(token) });
    });
}

app.Run();

record DevTokenReq(string email, string? tenantId, string? role);