using Ledger.Hebergement;
using Ledger.Query.Api.Composition;
using Ledger.Query.Api.Http;
using Ledger.Query.Api.Observabilite;
using Ledger.Query.Api.Routes;
using Ledger.Query.Api.Securite;

namespace Ledger.Query.Api;

/// <summary>Composition de l'API de requête : services, puis pipeline HTTP.</summary>
public static class HebergementApiRequete
{
    /// <summary>L'API ne reçoit que des requêtes GET : aucun corps n'est accepté.</summary>
    public const long TailleMaxCorps = 0;

    public static WebApplicationBuilder AjouterApiRequete(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = TailleMaxCorps);
        builder.AjouterTlsInterne();
        builder.AjouterObservabilite();
        builder.Services.AjouterConsultationsRequete(builder.Configuration, builder.Environment);
        builder.Services.AjouterAuthentificationRequete(builder.Configuration);
        builder.Services.AjouterHttpRequete();
        builder.Services.AjouterOpenApiRequete();
        return builder;
    }

    public static WebApplication UtiliserApiRequete(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseMiddleware<IntergicielCorrelation>();
        app.UseAuthentication();
        app.UseAuthorization();

        app.AssocierSondesSante();
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi(OpenApiRequete.RouteDocument).AllowAnonymous();
        }

        var v1 = app.MapGroup("/v1").RequireAuthorization(AuthentificationRequete.PolitiqueLecture);
        v1.AssocierComptes();
        v1.AssocierBalance();
        v1.AssocierEcritures();
        v1.AssocierPeriodes();
        v1.AssocierTaxes();
        return app;
    }
}
