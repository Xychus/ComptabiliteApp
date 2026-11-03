using Ledger.Pki.Application.Rotation;
using Ledger.Pki.Infrastructure.Aws;
using Ledger.Pki.Infrastructure.Memoire;

namespace Ledger.Pki.Infrastructure.Tests.Contrats;

public sealed class MagasinCertificatsMemoireContratTests : ContratMagasinCertificats
{
    private readonly MagasinCertificatsMemoire _memoire = new();

    public MagasinCertificatsMemoireContratTests() => _memoire.Creer(Secret, EtiquettesEssai);

    protected override IMagasinCertificats Magasin => _memoire;

    protected override void AmorcerRotation(string version) => _memoire.AmorcerRotation(Secret, version);
}

public sealed class MagasinCertificatsSecretsManagerContratTests : ContratMagasinCertificats, IDisposable
{
    private readonly MagasinCertificatsMemoire _memoire = new();
    private readonly SecretsManagerEssai _client;

    public MagasinCertificatsSecretsManagerContratTests()
    {
        _memoire.Creer(Secret, EtiquettesEssai);
        _client = new SecretsManagerEssai(_memoire);
        Magasin = new MagasinCertificatsSecretsManager(_client);
    }

    protected override IMagasinCertificats Magasin { get; }

    protected override void AmorcerRotation(string version) => _memoire.AmorcerRotation(Secret, version);

    public void Dispose() => _client.Dispose();
}
