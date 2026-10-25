using Amazon.ECS;
using Amazon.ECS.Model;
using Task = System.Threading.Tasks.Task;
using Ledger.Pki.Application.Rotation;

namespace Ledger.Pki.Infrastructure.Aws;

/// <summary>
/// Force un nouveau déploiement des services ECS qui consomment le certificat (étiquettes
/// <c>pki:grappe</c> et <c>pki:services</c>) : Kestrel, librdkafka et Kafka Connect ne relisent pas
/// un secret injecté au démarrage. Le disjoncteur de déploiement d'ECS protège d'un échec.
/// </summary>
public sealed class RedeploiementEcs(IAmazonECS ecs) : IActionApresRotation
{
    public const string EtiquetteGrappe = "pki:grappe";
    public const string EtiquetteServices = "pki:services";

    private static readonly char[] _separateurs = [' '];

    public string Nom => "redéploiement ECS";

    public bool SApplique(DescriptionSecret description)
    {
        ArgumentNullException.ThrowIfNull(description);
        return description.Etiquettes.ContainsKey(EtiquetteGrappe) && Services(description).Length > 0;
    }

    public async Task ExecuterAsync(DescriptionSecret description, string valeur, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(description);

        var grappe = description.Etiquettes[EtiquetteGrappe];
        foreach (var service in Services(description))
        {
            await ecs.UpdateServiceAsync(
                new UpdateServiceRequest { Cluster = grappe, Service = service, ForceNewDeployment = true },
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static string[] Services(DescriptionSecret description) =>
        description.Etiquettes.TryGetValue(EtiquetteServices, out var services)
            ? services.Split(_separateurs, StringSplitOptions.RemoveEmptyEntries)
            : [];
}
