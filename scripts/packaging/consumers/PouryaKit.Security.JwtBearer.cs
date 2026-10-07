using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using PouryaKit.Security;
using PouryaKit.Security.JwtBearer;

System.Type[] types = [typeof(PouryaKitJwtBearerOptions)];

// The packed bearer scheme, registered and configured as a Host would, issuer, audience and key set in code.
var services = new ServiceCollection().AddLogging().AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
services.AddPouryaKitJwtBearer(claims => claims.TenantClaim = "tenant");
services.Configure<JwtBearerOptions>(
    JwtBearerDefaults.AuthenticationScheme,
    bearer =>
    {
        bearer.TokenValidationParameters.ValidIssuer = "https://issuer.example";
        bearer.TokenValidationParameters.ValidAudience = "package-consumer";
        bearer.TokenValidationParameters.IssuerSigningKey = new SymmetricSecurityKey(new byte[32]);
    }
);
using var provider = services.BuildServiceProvider();
var bearer = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme);
Console.WriteLine(types.Length + (bearer.MapInboundClaims ? 0 : 1) + (PouryaKitClaimTypes.Tenant.Length > 0 ? 1 : 0));
