using NBitcoin;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using WalletWasabi.Blockchain.Analysis.Clustering;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Models;
using WalletWasabi.Tests.TestCommon;
using WalletWasabi.Wallets;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Wallets;

public sealed class WalletTransactionJournalTests : IDisposable
{
	private readonly string _directory = Path.Combine(TestDirectory.Get(), Guid.NewGuid().ToString("N"));

	[Fact]
	public void PendingTransactionAndItsInclusiveCursorSurviveReload()
	{
		var keys = CreateKeys();
		var transaction = CreateTransaction(keys);
		var journal = new WalletTransactionJournal(keys);
		journal.RecordPending(transaction, 100);
		journal.RecordPending(transaction, 150);

		var reloaded = new WalletTransactionJournal(keys);

		Assert.Equal(100u, reloaded.NextScanHeight);
		Assert.Empty(reloaded.GetPendingThrough(99));
		Assert.Equal(transaction.GetHash(), Assert.Single(reloaded.GetPendingThrough(100)));
		AssertTransaction(transaction, Assert.Single(reloaded.PendingTransactions));
	}

	[Fact]
	public void JournalIsNotDiscoveredAsAnotherWallet()
	{
		var directories = new WalletDirectories(Network.RegTest, _directory);
		var walletPath = directories.GetWalletFilePaths("journal-wallet").walletFilePath;
		var keys = CreateKeys(walletPath);
		new WalletTransactionJournal(keys).RecordPending(CreateTransaction(keys), 100);

		Assert.Equal(walletPath, Assert.Single(directories.EnumerateWalletFiles()).FullName);
	}

	[Fact]
	public void ScanProgressAndRewindArePersistedPerTransaction()
	{
		var keys = CreateKeys();
		var first = CreateTransaction(keys);
		var second = CreateTransaction(keys);
		var journal = new WalletTransactionJournal(keys);
		journal.RecordPending(first, 100);
		journal.RecordPending(second, 105);
		journal.CompleteScan(100, [first.GetHash()], [first, second]);

		journal = new WalletTransactionJournal(keys);
		Assert.Equal(101u, journal.NextScanHeight);
		Assert.Equal(first.GetHash(), Assert.Single(journal.GetPendingThrough(104)));
		Assert.Equal(2, journal.GetPendingThrough(105).Length);
		journal.CompleteScan(104, [first.GetHash()], [first, second]);
		journal.RewindPending(first, 102);

		journal = new WalletTransactionJournal(keys);
		Assert.Equal(102u, journal.NextScanHeight);
		Assert.Equal(first.GetHash(), Assert.Single(journal.GetPendingThrough(102)));
		journal.CompleteScan(102, [first.GetHash()], [second]);

		journal = new WalletTransactionJournal(keys);
		Assert.Equal(105u, journal.NextScanHeight);
		AssertTransaction(second, Assert.Single(journal.PendingTransactions));
	}

	[Fact]
	public void ConflictArchiveCanBeReloadedAndReleasedBackToPending()
	{
		var keys = CreateKeys();
		var parent = CreateTransaction(keys);
		var child = CreateTransaction(keys, new OutPoint(parent.GetHash(), 0));
		var blockHash = RandomUtils.GetUInt256();
		var confirmation = new SmartTransaction(CreateTransaction(keys).Transaction, new Height(101), blockHash);
		var journal = new WalletTransactionJournal(keys);
		journal.RecordPending(parent, 100);
		journal.RecordPending(child, 100);
		journal.RecordConflict(confirmation, [child, parent]);
		journal.RecordConflict(confirmation, [parent]);

		journal = new WalletTransactionJournal(keys);
		var conflict = Assert.Single(journal.Conflicts);
		Assert.Equal(blockHash.ToString(), conflict.BlockHash);
		Assert.Equal(101u, conflict.Height);
		Assert.Null(journal.NextScanHeight);
		Assert.Empty(journal.PendingTransactions);
		Assert.True(journal.IsInvalidated(parent.GetHash()));
		Assert.True(journal.IsInvalidated(child.GetHash()));
		var archived = journal.GetInvalidatedTransactions(conflict);
		Assert.Equal(2, archived.Count);
		AssertTransaction(parent, archived.Single(transaction => transaction.GetHash() == parent.GetHash()));
		AssertTransaction(child, archived.Single(transaction => transaction.GetHash() == child.GetHash()));
		journal.RecordPending(parent, 90);
		Assert.Empty(journal.PendingTransactions);

		journal.ReleaseConflict(conflict, 101);
		journal = new WalletTransactionJournal(keys);

		Assert.Empty(journal.Conflicts);
		Assert.False(journal.IsInvalidated(parent.GetHash()));
		Assert.False(journal.IsInvalidated(child.GetHash()));
		Assert.Equal(101u, journal.NextScanHeight);
		Assert.Equal(2, journal.GetPendingThrough(101).Length);
		AssertTransaction(parent, journal.PendingTransactions.Single(transaction => transaction.GetHash() == parent.GetHash()));
		AssertTransaction(child, journal.PendingTransactions.Single(transaction => transaction.GetHash() == child.GetHash()));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void AConflictRequiresBothConfirmationAndItsBlockHash(bool confirmed)
	{
		var keys = CreateKeys();
		var pending = CreateTransaction(keys);
		var candidate = new SmartTransaction(CreateTransaction(keys).Transaction,
			confirmed ? new Height(101) : Height.Mempool, confirmed ? null : RandomUtils.GetUInt256());
		var journal = new WalletTransactionJournal(keys);
		journal.RecordPending(pending, 100);

		Assert.Throws<ArgumentException>(() => journal.RecordConflict(candidate, [pending]));

		var reloaded = new WalletTransactionJournal(keys);
		Assert.Empty(reloaded.Conflicts);
		AssertTransaction(pending, Assert.Single(reloaded.PendingTransactions));
	}

	[Fact]
	public void SwitchingNetworksDiscardsPreviousNetworkState()
	{
		var keys = CreateKeys();
		new WalletTransactionJournal(keys).RecordPending(CreateTransaction(keys), 100);
		keys.AssertNetworkOrClearBlockState(Network.Main);

		var journal = new WalletTransactionJournal(keys);

		Assert.Empty(journal.PendingTransactions);
		Assert.Empty(journal.Conflicts);
		Assert.Null(journal.NextScanHeight);
	}

	[Fact]
	public void AnUnrecognizedNetworkNameIsRejected()
	{
		var keys = CreateKeys();
		new WalletTransactionJournal(keys).RecordPending(CreateTransaction(keys), 100);
		var state = ReadState(keys);
		state["Network"] = "not-a-bitcoin-network";
		WriteState(keys, state);

		Assert.Throws<InvalidOperationException>(() => new WalletTransactionJournal(keys));
	}

	[Theory]
	[InlineData("not-a-transaction-hash")]
	[InlineData("0000000000000000000000000000000000000000000000000000000000000000")]
	public void PendingHashMustMatchItsStoredTransaction(string hash)
	{
		var keys = CreateKeys();
		new WalletTransactionJournal(keys).RecordPending(CreateTransaction(keys), 100);
		var state = ReadState(keys);
		state["Pending"]![0]!["Transaction"]!["Hash"] = hash;
		WriteState(keys, state);
		Assert.Throws<InvalidOperationException>(() =>
		{
			var journal = new WalletTransactionJournal(keys);
			_ = journal.PendingTransactions.ToArray();
		});
	}

	[Fact]
	public void MalformedJournalJsonIsRejected()
	{
		var keys = CreateKeys();
		new WalletTransactionJournal(keys).RecordPending(CreateTransaction(keys), 100);
		File.WriteAllText(GetJournalPath(keys), "{");

		Assert.Throws<JsonException>(() => new WalletTransactionJournal(keys));
	}

	[Fact]
	public void FailedPendingWriteDoesNotPublishAndCanBeRetried()
	{
		var keys = CreateKeys();
		var transaction = CreateTransaction(keys);
		var journal = new WalletTransactionJournal(keys);
		var obstruction = BlockJournalWrites(keys);

		AssertFailedWrite(() => journal.RecordPending(transaction, 100));

		Assert.Empty(journal.PendingTransactions);
		Assert.Null(journal.NextScanHeight);
		Directory.Delete(obstruction);
		journal.RecordPending(transaction, 100);
		var reloaded = new WalletTransactionJournal(keys);
		AssertTransaction(transaction, Assert.Single(reloaded.PendingTransactions));
		Assert.Equal(100u, reloaded.NextScanHeight);
	}

	[Fact]
	public void FailedScanWritePreservesTheDurableCursorUntilRetry()
	{
		var keys = CreateKeys();
		var transaction = CreateTransaction(keys);
		var journal = new WalletTransactionJournal(keys);
		journal.RecordPending(transaction, 100);
		var obstruction = BlockJournalWrites(keys);

		AssertFailedWrite(() => journal.CompleteScan(100, [transaction.GetHash()], [transaction]));

		Assert.Equal(100u, journal.NextScanHeight);
		Assert.Equal(100u, new WalletTransactionJournal(keys).NextScanHeight);
		Directory.Delete(obstruction);
		journal.CompleteScan(100, [transaction.GetHash()], [transaction]);
		Assert.Equal(101u, new WalletTransactionJournal(keys).NextScanHeight);
	}

	[Fact]
	public void FailedConflictReleaseKeepsItsProofAndCanBeRetried()
	{
		var keys = CreateKeys();
		var transaction = CreateTransaction(keys);
		var confirmation = new SmartTransaction(CreateTransaction(keys).Transaction, new Height(101), RandomUtils.GetUInt256());
		var journal = new WalletTransactionJournal(keys);
		journal.RecordPending(transaction, 100);
		journal.RecordConflict(confirmation, [transaction]);
		var conflict = Assert.Single(journal.Conflicts);
		var obstruction = BlockJournalWrites(keys);

		AssertFailedWrite(() => journal.ReleaseConflict(conflict, 101));

		Assert.True(journal.IsInvalidated(transaction.GetHash()));
		Assert.Empty(journal.PendingTransactions);
		Assert.Single(new WalletTransactionJournal(keys).Conflicts);
		Directory.Delete(obstruction);
		journal.ReleaseConflict(conflict, 101);
		var reloaded = new WalletTransactionJournal(keys);
		Assert.Empty(reloaded.Conflicts);
		AssertTransaction(transaction, Assert.Single(reloaded.PendingTransactions));
		Assert.Equal(101u, reloaded.NextScanHeight);
	}

	private KeyManager CreateKeys(string? filePath = null) => KeyManager.CreateNew(out _, "", Network.RegTest, filePath ?? Path.Combine(_directory, "wallet.json"));

	private static SmartTransaction CreateTransaction(KeyManager keys, OutPoint? input = null)
	{
		var transaction = Network.RegTest.CreateTransaction();
		transaction.Inputs.Add(input ?? new OutPoint(RandomUtils.GetUInt256(), 0));
		transaction.Outputs.Add(Money.Satoshis(100_000), keys.GetNextReceiveKey("journal").P2wpkhScript);
		return new SmartTransaction(transaction, Height.Mempool, labels: new LabelsArray("invoice", "recipient"),
			isReplacement: true, isSpeedup: true, isCancellation: true,
			firstSeen: DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
	}

	private static void AssertTransaction(SmartTransaction expected, SmartTransaction actual)
	{
		Assert.Equal(expected.GetHash(), actual.GetHash());
		Assert.Equal(expected.Transaction.ToHex(), actual.Transaction.ToHex());
		Assert.Equal(Height.Mempool, actual.Height);
		Assert.Equal(expected.Labels.ToString(), actual.Labels.ToString());
		Assert.Equal(expected.FirstSeen, actual.FirstSeen);
		Assert.Equal(expected.IsReplacement, actual.IsReplacement);
		Assert.Equal(expected.IsSpeedup, actual.IsSpeedup);
		Assert.Equal(expected.IsCancellation, actual.IsCancellation);
	}

	private static string GetJournalPath(KeyManager keys) => WalletTransactionJournal.GetFilePath(keys.FilePath!);
	private static JsonNode ReadState(KeyManager keys) => JsonNode.Parse(File.ReadAllText(GetJournalPath(keys)))!;
	private static void WriteState(KeyManager keys, JsonNode state) => File.WriteAllText(GetJournalPath(keys), state.ToJsonString());
	private static string BlockJournalWrites(KeyManager keys) => Directory.CreateDirectory(GetJournalPath(keys) + ".new").FullName;

	private static void AssertFailedWrite(Action mutation)
	{
		var exception = Record.Exception(mutation);
		Assert.True(exception is IOException or UnauthorizedAccessException, exception?.ToString() ?? "The obstructed journal write unexpectedly succeeded.");
	}

	public void Dispose()
	{
		if (Directory.Exists(_directory)) { Directory.Delete(_directory, recursive: true); }
	}
}
