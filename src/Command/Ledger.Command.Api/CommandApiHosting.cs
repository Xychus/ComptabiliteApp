using Ledger.Command.Api.Composition;
using Ledger.Command.Api.Endpoints;
using Ledger.Command.Api.Http;
using Ledger.Command.Api.Observability;
using Ledger.Command.Api.Security;
using Ledger.Hebergement;

namespace Ledger.Command.Api;

/// <summary>Composition de l'API de commandes : services, puis pipeline HTTP.</summary>
public static class CommandApiHosting
{
    /// <summary>Taille maximale d'un corps de requête : une écriture tient largement dans 1 Mio.</summary>
    public const long MaxRequestBodySize = 1024 * 1024;

    public static WebApplicationBuilder AddCommandApi(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = MaxRequestBodySize);
        builder.AjouterTlsInterne();
        builder.AddLedgerObservability();
        builder.Services.AddLedgerCommands(builder.Configuration, builder.Environment);
        builder.Services.AddLedgerAuthentication(builder.Configuration);
        builder.Services.AddLedgerHttp();
        builder.Services.AddLedgerOpenApi();
        return builder;
    }

    public static WebApplication UseCommandApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapLedgerHealthChecks();
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi(LedgerOpenApi.DocumentRoute).AllowAnonymous();
        }

        var v1 = app.MapGroup("/v1").RequireAuthorization(LedgerAuthentication.WritePolicy);
        v1.MapJournalEntries();
        v1.MapPeriods();
        v1.MapAccounts();
        return app;
    }
}
