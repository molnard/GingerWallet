using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Moq;
using NBitcoin;
using WalletWasabi.Backend.Models;
using WalletWasabi.Blockchain.Blocks;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Blockchain.Mempool;
using WalletWasabi.Blockchain.TransactionProcessing;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Stores;
using WalletWasabi.Models;
using WalletWasabi.Tests.TestCommon;
using WalletWasabi.Wallets;
using WalletWasabi.Wallets.FilterProcessor;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Wallet;

/// <summary>
/// Tests for <see cref="WalletFilterProcessor"/>.
/// </summary>
public class WalletFilterProcessorTests
{
	private readonly Channel<NewFiltersEventTask> _eventChannel = Channel.CreateUnbounded<NewFiltersEventTask>();

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task InitializedWalletProtectsArchivedCreditAndPersistsReceiptsBeforeStartAsync(bool dispose)
	{
		var node = await MockNode.CreateNodeAsync();
		await using var builder = new WalletBuilder(node);
		using var template = await builder.CreateRealWalletBasedOnTestWalletAsync(new TestWallet("prestart", node.Rpc));
		var keys = template.KeyManager;
		keys.SetFilePath(Path.Combine(TestDirectory.Get(), $"prestart-{dispose}.json"));
		var archived = CreateIncomingTransaction(keys, 200_000);
		using Key foreignKey = new();
		var cancellation = Network.RegTest.CreateTransaction();
		cancellation.Inputs.Add(archived.Transaction.Inputs[0].PrevOut, new Script(OpcodeType.OP_0, OpcodeType.OP_0));
		cancellation.Outputs.Add(Money.Satoshis(190_000), foreignKey.PubKey.Hash.ScriptPubKey);
		new WalletTransactionJournal(keys).RecordConflict(new SmartTransaction(cancellation, new Height(101), RandomUtils.GetUInt256()), [archived]);
		template.Dispose();

		var store = template.BitcoinStore;
		using BlockDownloadService blocks = new(Mock.Of<IFileSystemBlockRepository>(), [], p2pBlockProvider: null);
		var transactions = new TransactionProcessor(store.TransactionStore, store.MempoolService, keys, template.ServiceConfiguration.DustThreshold);
		using var filters = new WalletFilterProcessor(keys, store, transactions, blocks);
		using WalletWasabi.Wallets.Wallet wallet = new(builder.DataDir, Network.RegTest, keys, store, template.Synchronizer,
			template.ServiceConfiguration, template.FeeProvider, transactions, filters, template.UnconfirmedTransactionChainProvider);

		wallet.Initialize();

		Assert.Equal(WalletState.Initialized, wallet.State);
		Assert.Null(filters.ExecuteTask);
		store.MempoolService.Process(archived.Transaction);
		Assert.Empty(wallet.Coins);
		Assert.False(store.TransactionStore.MempoolStore.Contains(archived.GetHash()));
		var received = CreateIncomingTransaction(keys, 300_000);
		var inclusiveHeight = (uint)keys.GetBestHeight().Value;
		store.MempoolService.Process(received.Transaction);
		Assert.Equal(received.GetHash(), Assert.Single(wallet.Coins).TransactionId);
		var persisted = new WalletTransactionJournal(keys);
		Assert.True(persisted.IsInvalidated(archived.GetHash()));
		Assert.Equal(received.GetHash(), Assert.Single(persisted.PendingTransactions).GetHash());
		Assert.Equal(inclusiveHeight, persisted.NextScanHeight);
		Assert.Null(filters.ExecuteTask);

		// An event publisher can already have captured its delegate when teardown
		// unsubscribes. That callback must remain harmless after tracking detaches.
		var captured = Assert.IsType<EventHandler<SmartTransaction>>(typeof(MempoolService)
			.GetField("TransactionReceived", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store.MempoolService));
		if (dispose) { wallet.Dispose(); }
		else { await wallet.StopAsync(CancellationToken.None); }
		Assert.Null(transactions.IsInvalidatedTransaction);
		var late = CreateIncomingTransaction(keys, 400_000);
		captured(store.MempoolService, late);
		Assert.Equal(received.GetHash(), Assert.Single(wallet.Coins).TransactionId);
		Assert.False(store.TransactionStore.MempoolStore.Contains(late.GetHash()));
		wallet.Dispose();
	}

	private static SmartTransaction CreateIncomingTransaction(KeyManager keys, long satoshis)
	{
		var raw = Network.RegTest.CreateTransaction();
		raw.Inputs.Add(new OutPoint(RandomUtils.GetUInt256(), 0));
		raw.Outputs.Add(Money.Satoshis(satoshis), keys.GetNextReceiveKey("prestart").P2wpkhScript);
		return new SmartTransaction(raw, Height.Mempool);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task CanceledStartupCompletesPendingRequestsAsync(bool dispose)
	{
		SmartHeaderChain headerChain = new();
		await using IndexStore indexStore = new(TestDirectory.Get(), Network.RegTest, headerChain);
		await using AllTransactionStore transactionStore = new(SqliteStorageHelper.InMemoryDatabase, Network.RegTest);
		await transactionStore.InitializeAsync();
		var keyManager = KeyManager.CreateNew(out _, "", Network.RegTest);
		var transactionProcessor = new TransactionProcessor(transactionStore, null, keyManager, Money.Zero);
		BitcoinStore bitcoinStore = new(indexStore, null!, null!, headerChain, null!);
		using BlockDownloadService blockDownloadService = new(Mock.Of<IFileSystemBlockRepository>(), [], p2pBlockProvider: null);
		using WalletFilterProcessor processor = new(keyManager, bitcoinStore, transactionProcessor, blockDownloadService);
		var request = processor.ProcessAsync(CancellationToken.None);

		await processor.StartAsync(new CancellationToken(canceled: true));
		var concurrentRequests = Enumerable.Range(0, 32)
			.Select(_ => Task.Run(() => processor.ProcessAsync(CancellationToken.None)))
			.ToArray();
		if (dispose)
		{
			processor.Dispose();
		}
		else
		{
			await processor.StopAsync(CancellationToken.None);
		}

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(TimeSpan.FromSeconds(5)));
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.WhenAll(concurrentRequests).WaitAsync(TimeSpan.FromSeconds(5)));
		Assert.All(concurrentRequests, task => Assert.True(task.IsCanceled));
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processor.ProcessAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
	}

	[Fact]
	public async Task TestFilterProcessingAsync()
	{
		using CancellationTokenSource testDeadlineCts = new(TimeSpan.FromMinutes(5));

		var node = await MockNode.CreateNodeAsync();
		var wallet = new TestWallet("wallet", node.Rpc);
		await using var builder = new WalletBuilder(node);

		await node.GenerateBlockAsync(testDeadlineCts.Token);

		foreach (var _ in Enumerable.Range(0, 1001))
		{
			await node.GenerateBlockAsync(testDeadlineCts.Token);
		}

		var allFilters = node.BuildFilters().ToList();

		// The MinGapLimit will generate some keys
		using var realWallet = await builder.CreateRealWalletBasedOnTestWalletAsync(wallet, 2000);

		// Process all but the last 4 which will be processed through events during the synchronization.
		await realWallet.BitcoinStore.IndexStore.AddNewFiltersAsync(allFilters.Take(allFilters.Count - 4).Where(x => x.Header.Height > 101));

		realWallet.BitcoinStore.IndexStore.NewFilters += (_, filters) => Wallet_NewFiltersEmulator(realWallet.WalletFilterProcessor);

		// Mock the database
		foreach (var filter in allFilters)
		{
			realWallet.WalletFilterProcessor.FilterIterator.Cache[filter.Header.Height] = filter;
		}

		await realWallet.WalletFilterProcessor.StartAsync(testDeadlineCts.Token);

		List<Task> allTasks = new();

		// This emulates the synchronization
		var firstTask = Task.Run(async () => await realWallet.WalletFilterProcessor.ProcessAsync(testDeadlineCts.Token), testDeadlineCts.Token);
		allTasks.Add(firstTask);

		// This emulates receiving some new filters while first synchronization is being processed.
		await realWallet.BitcoinStore.IndexStore.AddNewFiltersAsync(new List<FilterModel>() { allFilters.ElementAt(allFilters.Count - 4) });
		var firstExtraFilter = await _eventChannel.Reader.ReadAsync(testDeadlineCts.Token);
		allTasks.Add(firstExtraFilter.Task);

		await realWallet.BitcoinStore.IndexStore.AddNewFiltersAsync(new List<FilterModel>() { allFilters.ElementAt(allFilters.Count - 3) });
		var secondExtraFilter = await _eventChannel.Reader.ReadAsync(testDeadlineCts.Token);
		allTasks.Add(secondExtraFilter.Task);

		// Sync should take some time.
		Assert.False(firstTask.IsCompleted);

		await firstTask;

		// This emulates final synchronization
		var secondTask = Task.Run(async () => await realWallet.WalletFilterProcessor.ProcessAsync(testDeadlineCts.Token), testDeadlineCts.Token);
		allTasks.Add(secondTask);

		// This emulates receiving some new filters while final synchronization is being processed.
		await realWallet.BitcoinStore.IndexStore.AddNewFiltersAsync(new List<FilterModel>() { allFilters.ElementAt(allFilters.Count - 2) });
		var thirdExtraFilter = await _eventChannel.Reader.ReadAsync(testDeadlineCts.Token);
		allTasks.Add(thirdExtraFilter.Task);

		await realWallet.BitcoinStore.IndexStore.AddNewFiltersAsync(new List<FilterModel>() { allFilters.ElementAt(allFilters.Count - 1) });
		var fourthExtraFilter = await _eventChannel.Reader.ReadAsync(testDeadlineCts.Token);
		allTasks.Add(fourthExtraFilter.Task);

		var whenAll = Task.WhenAll(allTasks);
		// All tasks should finish
		await whenAll;

		// Blockchain Tip should be reached
		Assert.Equal(realWallet.BitcoinStore.SmartHeaderChain.TipHeight, (uint)realWallet.KeyManager.GetBestHeight().Value);
	}

	// This emulates the NewFiltersProcessed event
	private void Wallet_NewFiltersEmulator(WalletFilterProcessor walletFilterProcessor)
	{
		// Initiate tasks and write tasks to the channel to pass them back to the test.
		// Underlying tasks without cancellation token as it works on Wallet.
		Task task = walletFilterProcessor.ProcessAsync(CancellationToken.None);
		_eventChannel.Writer.TryWrite(new NewFiltersEventTask(task));
	}

	private record NewFiltersEventTask(Task Task);
}
