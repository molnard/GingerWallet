using System.IO;
using System.Linq;
using System.Security;
using System.Threading.Tasks;
using System.Diagnostics.CodeAnalysis;
using NBitcoin;
using WalletWasabi.Blockchain.Analysis.Clustering;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Blockchain.TransactionBuilding;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Blockchain.TransactionProcessing;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Fluent.Models.Wallets;
using WalletWasabi.Tests.Helpers;
using WalletWasabi.Tests.TestCommon;
using WalletWasabi.Userfacing;
using WalletWasabi.Wallets.Slip39;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Wallets;

public class MultiShareKeyManagementTests
{
	[Theory]
	[InlineData("")]
	[InlineData(" leading and trailing ")]
	[InlineData(" ")]
	public async Task RealLoginAuthorizationAndSigningPreserveExactPassphraseAfterEncryptedReload(string password)
	{
		var shares = Shamir.Generate(2, 3, "ABCDEFGHIJKLMNOP"u8.ToArray());
		var directory = TestDirectory.Get();
		var path = Path.Combine(directory, "auth-multi-share.json");
		var manager = KeyManager.RecoverMultiShare(shares, password, Network.Main, filePath: path);
		manager.EncryptionKey = "01234567890123456789012345678901";
		manager.ToFile();
		var loaded = KeyManager.FromFile(path, manager.EncryptionKey);
		Assert.True(PasswordHelper.TryPassword(loaded, password, out var compatibility));
		Assert.Null(compatibility);
		var processor = new TransactionProcessor(null!, null, loaded, Money.Zero);
		using var wallet = new WalletWasabi.Wallets.Wallet(directory, Network.Main, loaded, null!, null!, null!, null!, processor, null!, null!);
		var auth = new WalletAuthModel(null!, wallet);
		await auth.LoginAsync(password);
		Assert.True(auth.IsLoggedIn);
		Assert.True(await auth.TryPasswordAsync(password));
		Assert.Equal(password, wallet.Kitchen.SaltSoup());
		if (password.Trim() != password) { Assert.False(await auth.TryPasswordAsync(password.Trim())); }
		var coins = ServiceFactory.CreateCoins(TestRandom.Get(), loaded, [("sender", 0, 1m, true, 1)]);
		var factory = new TransactionFactory(Network.Main, loaded, new CoinsView(coins), new CoinStore(coins), wallet.Kitchen.SaltSoup());
		var destination = BitcoinAddress.Create("bc1q7zqqsmqx5ymhd7qn73lm96w5yqdkrmx7fdevah", Network.Main);
		var parameters = TransactionParametersBuilder.CreateDefault().SetPayment(new PaymentIntent(destination, Money.Coins(0.5m))).SetFeeRate(2m).Build();
		Assert.True(factory.BuildTransaction(parameters).Psbt.IsAllFinalized());
		auth.Logout();
		Assert.False(auth.IsLoggedIn);
	}

	[Fact]
	public void RejectsCorruptedRedundantMembersAndGroupsEvenWithValidMnemonicChecksums()
	{
		var shares = Shamir.Generate(2, 3, "ABCDEFGHIJKLMNOP"u8.ToArray());
		var corrupt = shares[2].Value.ToArray();
		corrupt[0] ^= 1;
		shares[2] = Share.FromMnemonic((shares[2] with { Value = corrupt }).ToMnemonic(WordList.Wordlist));
		Assert.Throws<ArgumentException>(() => Shamir.SelectRecoveryShares(shares));
		var groups = Shamir.Generate(2, [(1, 1), (1, 1), (1, 1)], "ABCDEFGHIJKLMNOP"u8.ToArray());
		corrupt = groups[2].Value.ToArray();
		corrupt[0] ^= 1;
		groups[2] = Share.FromMnemonic((groups[2] with { Value = corrupt }).ToMnemonic(WordList.Wordlist));
		Assert.Throws<ArgumentException>(() => Shamir.SelectRecoveryShares(groups));
	}

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

	private class CoinStore(SmartCoin[] coins) : ITransactionStore
	{
		public bool TryGetTransaction(uint256 hash, [NotNullWhen(true)] out SmartTransaction? transaction)
		{
			transaction = coins.FirstOrDefault(c => c.TransactionId == hash)?.Transaction;
			return transaction is not null;
		}
	}
}
