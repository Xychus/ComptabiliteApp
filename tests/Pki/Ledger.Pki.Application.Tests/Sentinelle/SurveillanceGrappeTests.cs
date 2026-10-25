using Ledger.Pki.Application.Emission;
using Ledger.Pki.Application.Sentinelle;
using Ledger.Pki.Tests.Support;

namespace Ledger.Pki.Application.Tests.Sentinelle;

public sealed class SurveillanceGrappeTests
{
    [Fact]
    public async Task ExecuterAsync_collecte_avec_le_certificat_courant_et_publie_sous_le_nom_du_noeud()
    {
        var contenu = new ContenuSecretCertificat("c", "c", "k", "r", AutoriteEssai.Maintenant);
        var collecte = new CollecteEssai();
        var publicateur = new PublicateurEssai();

        var mesures = await new SurveillanceGrappe(new SourceEssai(contenu), collecte, publicateur, "kafka-3").ExecuterAsync(TestContext.Current.CancellationToken);

        Assert.Same(contenu, collecte.IdentiteUtilisee);
        Assert.Equal(("kafka-3", mesures), publicateur.Publie);
    }

    private sealed class SourceEssai(ContenuSecretCertificat contenu) : ISourceCertificatCourant
    {
        public Task<CertificatCourant> LireAsync(CancellationToken cancellationToken) => Task.FromResult(new CertificatCourant("v1", contenu));

        public Task<string> LireVersionAsync(CancellationToken cancellationToken) => Task.FromResult("v1");
    }

    private sealed class CollecteEssai : ICollecteMesuresGrappe
    {
        public ContenuSecretCertificat? IdentiteUtilisee { get; private set; }

        public Task<MesuresGrappe> CollecterAsync(ContenuSecretCertificat identite, CancellationToken cancellationToken)
        {
            IdentiteUtilisee = identite;
            return Task.FromResult(new MesuresGrappe(3, 0, 80, new Dictionary<string, long>()));
        }
    }

    private sealed class PublicateurEssai : IPublicateurMesuresGrappe
    {
        public (string Noeud, MesuresGrappe Mesures)? Publie { get; private set; }

        public Task PublierAsync(string noeud, MesuresGrappe mesures, CancellationToken cancellationToken)
        {
            Publie = (noeud, mesures);
            return Task.CompletedTask;
        }
    }
}
