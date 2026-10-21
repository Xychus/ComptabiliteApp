using Ledger.Query.Application.Consultations;
using Ledger.Query.Application.Fraicheur;
using Microsoft.Extensions.DependencyInjection;

namespace Ledger.Query.Application;

/// <summary>Consultations de l'API de requête. Les implémentations des ports de lecture viennent de l'infrastructure.</summary>
public static class ConsultationsServiceCollectionExtensions
{
    public static IServiceCollection AjouterConsultations(this IServiceCollection services, OptionsFraicheur options)
    {
        ArgumentNullException.ThrowIfNull(options);
        services.AddSingleton(options);
        services.AddSingleton<EvaluateurFraicheur>();
        services.AddSingleton<ConsulterSoldeCompte>();
        services.AddSingleton<ConsulterBalanceVerification>();
        services.AddSingleton<ConsulterLignesCompte>();
        services.AddSingleton<ConsulterEcriture>();
        services.AddSingleton<ConsulterPeriode>();
        services.AddSingleton<ConsulterDeclarationTpsTvq>();
        return services;
    }
}
