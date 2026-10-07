using System.IO;
using System.Linq;
using System.Security;
using NBitcoin;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Tests.TestCommon;
using WalletWasabi.Wallets.Slip39;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Wallets;

public class MultiShareKeyManagementTests
{
	[Theory]
	[InlineData("")]
	[InlineData(" leading and trailing ")]
	[InlineData(" ")]
	public void PrintableAsciiPassphrasesArePreserved(string password)
	{
		var shares = Shamir.Generate(2, 3, "ABCDEFGHIJKLMNOP"u8.ToArray());
		var first = KeyManager.RecoverMultiShare(shares[..2], password, Network.Main);
		var second = KeyManager.RecoverMultiShare(shares[1..], password, Network.Main);
		Assert.Equal(first.GetMasterExtKey(password), second.GetMasterExtKey(password));
	}

	[Fact]
	public void EveryThresholdSubsetAndRedundantSharesRecoverTheSameWallet()
	{
		var seed = "ABCDEFGHIJKLMNOP"u8.ToArray();
		var shares = Shamir.Generate(2, 3, seed);
		var first = KeyManager.RecoverMultiShare(shares[..2], "password", Network.Main);
		var last = KeyManager.RecoverMultiShare(shares[1..], "password", Network.Main);
		var all = KeyManager.RecoverMultiShare(shares, "password", Network.Main);
		Assert.Equal(first.SegwitExtPubKey, last.SegwitExtPubKey);
		Assert.Equal(first.TaprootExtPubKey, all.TaprootExtPubKey);
		Assert.Equal(first.GetMasterExtKey("password"), all.GetMasterExtKey("password"));
		Assert.Throws<SecurityException>(() => first.GetMasterExtKey("wrong password"));
		Assert.NotEqual(first.SegwitExtPubKey, KeyManager.RecoverMultiShare(shares, "different", Network.Main).SegwitExtPubKey);
	}

	[Fact]
	public void PersistedWalletRetainsBackupTypeAndTwoFactorEncryption()
	{
		var shares = Shamir.Generate(2, 3, "ABCDEFGHIJKLMNOP"u8.ToArray());
		var path = Path.Combine(TestDirectory.Get(), "multi-share.json");
		var wallet = KeyManager.RecoverMultiShare(shares, "password", Network.Main, filePath: path);
		wallet.EncryptionKey = "01234567890123456789012345678901";
		wallet.ToFile();
		var loaded = KeyManager.FromFile(path, wallet.EncryptionKey);
		Assert.True(loaded.IsMultiShareBackup);
		Assert.Equal(wallet.GetMasterExtKey("password"), loaded.GetMasterExtKey("password"));
		Assert.DoesNotContain(shares[0].ToMnemonic(WordList.Wordlist), File.ReadAllText(path));
	}

	[Fact]
	public void RejectsDuplicateMixedAndInvalidShares()
	{
		var shares = Shamir.Generate(2, 3, "ABCDEFGHIJKLMNOP"u8.ToArray());
		Assert.Throws<ArgumentException>(() => Shamir.SelectRecoveryShares([shares[0], shares[0]]));
		Assert.Throws<ArgumentException>(() => Shamir.SelectRecoveryShares([shares[0], shares[1] with { Extendable = false }]));
		Assert.Throws<ArgumentException>(() => Shamir.Generate(1, [(2, 2)], "ABCDEFGHIJKLMNOP"u8.ToArray(), iterationExponent: 16));
		Assert.Throws<NotSupportedException>(() => Shamir.Combine(shares[..2], "non-ASCII-\u00e9"));
		Assert.False(KeyManager.CreateNew(out _, "", Network.Main).IsMultiShareBackup);
	}
}
