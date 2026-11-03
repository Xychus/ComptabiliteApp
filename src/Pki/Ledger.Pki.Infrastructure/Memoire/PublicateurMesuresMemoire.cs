using Ledger.Pki.Application.Inventaire;

namespace Ledger.Pki.Infrastructure.Memoire;

/// <summary>Publicateur en mémoire : conserve les mesures publiées, pour les tests.</summary>
public sealed class PublicateurMesuresMemoire : IPublicateurMesures
{
    private readonly List<MesureEcheance> _publiees = [];

    public IReadOnlyList<MesureEcheance> Publiees => _publiees;

    public Task PublierAsync(IReadOnlyList<MesureEcheance> mesures, CancellationToken cancellationToken)
    {
        _publiees.AddRange(mesures);
        return Task.CompletedTask;
    }
}
