using Ledger.Contracts.Integrity;

namespace Ledger.Contracts.Tests.Integrity;

/// <summary>
/// Racine de Merkle du sceau de période (ADR 0005 et 0013). Les racines attendues ont été
/// recalculées indépendamment (Python <c>hashlib.sha256</c>) : les modifier invalide tous les sceaux.
/// </summary>
public sealed class MerkleTreeTests
{
    // SHA-256 de « a », « b », « c », « d », « e ».
    private const string A = "ca978112ca1bbdcafac231b39a23dc4da786eff8147c4e72b9807785afee48bb";
    private const string B = "3e23e8160039594a33894f6564e1b1348bbd7a0088d42c4acb73eeaed59c009d";
    private const string C = "2e7d2c03a9507ae265ecf5b5356885a53393a2029d241394997265a1a25aefc6";
    private const string D = "18ac3e7343f016890c510e93f935261169d9e3f565436429830faf0934f4f8e4";
    private const string E = "3f79bb7b435b05321651daefd374cdc681dc06faa65e374e38337b88ca046dea";

    [Fact]
    public void Une_liste_vide_a_pour_racine_le_hash_de_la_chaine_vide() =>
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", MerkleTree.Root([]));

    [Fact]
    public void Une_seule_feuille_est_sa_propre_racine() =>
        Assert.Equal(A, MerkleTree.Root([A]));

    [Fact]
    public void Le_dernier_noeud_impair_est_promu_tel_quel()
    {
        Assert.Equal("7075152d03a5cd92104887b476862778ec0c87be5c2fa1c0a90f87c49fad6eff", MerkleTree.Root([A, B, C]));
        Assert.Equal("d71f8983ad4ee170f8129f1ebcdd7440be7798d8e1c80420bf11f1eced610dba", MerkleTree.Root([A, B, C, D, E]));
    }

    [Fact]
    public void L_ordre_des_feuilles_compte() =>
        Assert.NotEqual(MerkleTree.Root([A, B, C]), MerkleTree.Root([B, A, C]));

    [Theory]
    [InlineData("abc")]
    [InlineData("CA978112CA1BBDCAFAC231B39A23DC4DA786EFF8147C4E72B9807785AFEE48BB")]
    [InlineData("za978112ca1bbdcafac231b39a23dc4da786eff8147c4e72b9807785afee48bb")]
    public void Un_hash_mal_forme_est_refuse(string leaf) =>
        Assert.Throws<ArgumentException>(() => MerkleTree.Root([A, leaf]));
}
