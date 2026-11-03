using Ledger.Pki.Application.Profils;

namespace Ledger.Pki.Application.Tests.Profils;

public sealed class ProfilCertificatTests
{
    private static Dictionary<string, string> Etiquettes(string? cn = "kafka-1", string? san = "kafka-1.ledger-dev.interne", string? usage = "serveur-client")
    {
        var etiquettes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (cn is not null)
        {
            etiquettes[ProfilCertificat.EtiquetteNomCommun] = cn;
        }

        if (san is not null)
        {
            etiquettes[ProfilCertificat.EtiquetteNomsDns] = san;
        }

        if (usage is not null)
        {
            etiquettes[ProfilCertificat.EtiquetteUsage] = usage;
        }

        return etiquettes;
    }

    [Fact]
    public void DepuisEtiquettes_profil_complet_est_lu()
    {
        var profil = ProfilCertificat.DepuisEtiquettes(Etiquettes(san: "kafka-1.ledger-dev.interne  kafka-1")).Value;

        Assert.Equal("kafka-1", profil.NomCommun);
        Assert.Equal(["kafka-1.ledger-dev.interne", "kafka-1"], profil.NomsDns);
        Assert.Equal(UsageCertificat.Serveur | UsageCertificat.Client, profil.Usages);
    }

    [Fact]
    public void DepuisEtiquettes_client_sans_nom_dns_est_accepte()
    {
        var profil = ProfilCertificat.DepuisEtiquettes(Etiquettes(cn: "debezium", san: null, usage: "client")).Value;

        Assert.Equal(UsageCertificat.Client, profil.Usages);
        Assert.Empty(profil.NomsDns);
    }

    [Theory]
    [InlineData(null, "pki.profil.nom-commun")]
    [InlineData("", "pki.profil.nom-commun")]
    [InlineData("CN=kafka", "pki.profil.nom-commun")]
    public void DepuisEtiquettes_nom_commun_invalide_est_refuse(string? cn, string code) =>
        Assert.Equal(code, ProfilCertificat.DepuisEtiquettes(Etiquettes(cn: cn)).Error.Code);

    [Theory]
    [InlineData("*.ledger-dev.interne")]
    [InlineData(".ledger")]
    [InlineData("-ledger")]
    [InlineData("ledger.")]
    public void DepuisEtiquettes_nom_dns_invalide_ou_joker_est_refuse(string san) =>
        Assert.Equal("pki.profil.nom-dns", ProfilCertificat.DepuisEtiquettes(Etiquettes(san: san)).Error.Code);

    [Theory]
    [InlineData(null)]
    [InlineData("tout")]
    public void DepuisEtiquettes_usage_invalide_est_refuse(string? usage) =>
        Assert.Equal("pki.profil.usage", ProfilCertificat.DepuisEtiquettes(Etiquettes(usage: usage)).Error.Code);

    [Fact]
    public void DepuisEtiquettes_serveur_sans_nom_dns_est_refuse() =>
        Assert.Equal("pki.profil.nom-dns", ProfilCertificat.DepuisEtiquettes(Etiquettes(san: null, usage: "serveur")).Error.Code);
}
