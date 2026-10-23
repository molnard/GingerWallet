using Microsoft.Extensions.Hosting;
using NBitcoin;
using Nito.AsyncEx;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Backend.Models;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Blockchain.TransactionProcessing;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Filter;
using WalletWasabi.Logging;
using WalletWasabi.Models;
using WalletWasabi.Services.Terminate;
using WalletWasabi.Stores;
using WalletWasabi.Wallets.BlockProvider;
using WalletWasabi.Wallets.FilterProcessor;

namespace WalletWasabi.Wallets;

/// <summary>
/// Service that keeps processing block filters.
/// </summary>
/// <seealso href="https://github.com/zkSNACKs/WalletWasabi/issues/10219">TurboSync specification.</seealso>
public class WalletFilterProcessor : BackgroundService
{
	/// <remarks>Guarded by <see cref="Lock"/>.</remarks>
	private FilterModel? _lastProcessedFilter;
	private WalletTransactionJournal? _transactionJournal;

	public WalletFilterProcessor(KeyManager keyManager, BitcoinStore bitcoinStore, TransactionProcessor transactionProcessor, BlockDownloadService blockDownloadService)
	{
		KeyManager = keyManager;
		BitcoinStore = bitcoinStore;
		TransactionProcessor = transactionProcessor;
		BlockDownloadService = blockDownloadService;

		FilterIterator = new BlockFilterIterator(BitcoinStore.IndexStore);
	}

	/// <remarks>Guarded by <see cref="Lock"/>.</remarks>
	private PriorityQueue<SyncRequest, Priority> SynchronizationRequests { get; } = new(Priority.Comparer);

	/// <remarks>Guards <see cref="SynchronizationRequests"/> and <see cref="_lastProcessedFilter"/>.</remarks>
	private object Lock { get; } = new();

	/// <remarks>Guarded by <see cref="Lock"/>.</remarks>
	private bool RequestsStopped { get; set; }

	private SemaphoreSlim SynchronizationRequestsSemaphore { get; } = new(initialCount: 0);

	private KeyManager KeyManager { get; }
	private BitcoinStore BitcoinStore { get; }
	private TransactionProcessor TransactionProcessor { get; }
	private BlockDownloadService BlockDownloadService { get; }
	private WalletTransactionJournal TransactionJournal => _transactionJournal ??= new WalletTransactionJournal(KeyManager);
	private bool PendingTransactionsInitialized { get; set; }

	private IEnumerable<SmartTransaction> GetPendingTransactions() => TransactionProcessor.Coins.AsAllCoinsView()
		.SelectMany(x => new[] { x.Transaction, x.SpenderTransaction })
		.OfType<SmartTransaction>().Where(x => !x.Confirmed).Distinct();

	private void UnconfirmedTransactionDetected(SmartTransaction transaction)
	{
		// The processor holds its lock until this cursor and the transaction have both been persisted.
		var startHeight = (uint)KeyManager.GetBestHeight().Value;
		TransactionJournal.RecordPending(transaction, startHeight);
		if (PendingTransactionsInitialized)
		{
			EnqueueSynchronization();
		}
	}

	/// <summary>Load pending transactions before scanning filters, without treating a backend's mempool as consensus evidence.</summary>
	public async Task LoadUnconfirmedTransactionsAsync(CancellationToken cancellationToken)
	{
		using (await ReorgLock.LockAsync(cancellationToken).ConfigureAwait(false))
		{
			foreach (var conflict in TransactionJournal.Conflicts.ToArray())
			{
				if (conflict.Height > BitcoinStore.SmartHeaderChain.TipHeight)
				{
					// A behind or temporarily shorter backend is not evidence that a previously confirmed conflict disappeared.
					continue;
				}
				var filters = await BitcoinStore.IndexStore.FetchBatchAsync(conflict.Height, 1, cancellationToken).ConfigureAwait(false);
				if (filters.Length == 0 || filters[0].Header.Height != conflict.Height)
				{
					throw new InvalidOperationException($"Cannot verify the canonical block at wallet conflict height {conflict.Height}.");
				}
				if (filters[0].Header.BlockHash.ToString() != conflict.BlockHash)
				{
					var canonical = filters[0];
					var block = await KeepTryingToGetBlockAsync(canonical.Header.BlockHash, new Priority(conflict.Height), cancellationToken).ConfigureAwait(false);
					var current = await BitcoinStore.IndexStore.FetchBatchAsync(conflict.Height, 1, cancellationToken).ConfigureAwait(false);
					if (block.GetHash() != canonical.Header.BlockHash || !block.Check() || current.Length == 0
						|| current[0].Header.Height != conflict.Height || current[0].Header.BlockHash != canonical.Header.BlockHash)
					{
						throw new InvalidOperationException("The active chain changed while verifying a wallet conflict.");
					}
					RestoreReorgedConflict(conflict);
				}
			}

			lock (TransactionProcessor.Lock)
			{
				var transactions = TransactionJournal.PendingTransactions
					.Concat(BitcoinStore.TransactionStore.MempoolStore.GetTransactions())
					.Where(x => !TransactionJournal.IsInvalidated(x.GetHash()))
					.Distinct().ToArray();
				foreach (var transaction in transactions)
				{
					// Also honor an explicit wallet rescan that rewinds behind a saved pending cursor.
					TransactionJournal.RewindPending(transaction, (uint)KeyManager.GetBestHeight().Value);
				}
				ProcessInDependencyOrder(transactions);
				PendingTransactionsInitialized = true;
			}
		}
	}

	private void RestoreReorgedConflict(WalletTransactionJournal.Conflict conflict)
	{
		lock (TransactionProcessor.Lock)
		{
			var precedingHeight = conflict.Height == 0 ? 0 : conflict.Height - 1;
			KeyManager.SetMaxBestHeight(new Height(precedingHeight));
			TransactionProcessor.UndoBlock(new Height(conflict.Height));
			BitcoinStore.TransactionStore.ReleaseToMempoolFromBlock(uint256.Parse(conflict.BlockHash));
			RestoreConflict(conflict, Math.Min(conflict.Height, (uint)KeyManager.GetBestHeight().Value));
		}
	}

	private void RestoreConflict(WalletTransactionJournal.Conflict conflict, uint inclusiveHeight)
	{
		lock (TransactionProcessor.Lock)
		{
			var transactions = TransactionJournal.GetInvalidatedTransactions(conflict);
			TransactionJournal.ReleaseConflict(conflict, inclusiveHeight);
			foreach (var transaction in transactions)
			{
				TransactionProcessor.ForgetTransaction(transaction.GetHash());
			}
			ProcessInDependencyOrder(transactions);
		}
	}

	private void ProcessInDependencyOrder(IEnumerable<SmartTransaction> transactions)
	{
		// Parents have to be present before their descendants restore wallet inputs.
		var remaining = transactions.ToList();
		while (remaining.Count > 0)
		{
			var next = remaining.First(x => x.Transaction.Inputs.All(i => remaining.All(t => t.GetHash() != i.PrevOut.Hash)));
			TransactionProcessor.Process(next);
			remaining.Remove(next);
		}
	}

	public FilterModel? LastProcessedFilter
	{
		get
		{
			lock (Lock)
			{
				return _lastProcessedFilter;
			}
		}
		set
		{
			lock (Lock)
			{
				_lastProcessedFilter = value;
			}
		}
	}

	/// <remarks>Internal only to allow modifications in tests.</remarks>
	internal BlockFilterIterator FilterIterator { get; }

	/// <summary>Make sure we don't process any request while a reorg is happening.</summary>
	private AsyncLock ReorgLock { get; } = new();

	public async Task ProcessAsync(CancellationToken cancellationToken)
	{
		var request = EnqueueSynchronization();
		await request.Tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
	}

	private SyncRequest EnqueueSynchronization()
	{
		SyncRequest request = new(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
		Priority priority = new();

		lock (Lock)
		{
			if (RequestsStopped)
			{
				request.Tcs.TrySetCanceled(CancellationToken.None);
			}
			else
			{
				SynchronizationRequests.Enqueue(request, priority);
				SynchronizationRequestsSemaphore.Release(releaseCount: 1);
			}
		}

		return request;
	}

	/// <inheritdoc />
	/// <summary>Used for filter synchronization.</summary>
	protected override async Task ExecuteAsync(CancellationToken cancellationToken)
	{
		try
		{
			while (true)
			{
				await SynchronizationRequestsSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

				SyncRequest? request;

				lock (Lock)
				{
					if (!SynchronizationRequests.TryPeek(out request, out _))
					{
						continue;
					}
				}

				try
				{
					bool reachedBlockChainTip;
					using (await ReorgLock.LockAsync(cancellationToken).ConfigureAwait(false))
					{
						Height lastHeight = KeyManager.GetBestHeight();
						uint? pendingHeight;
						lock (TransactionProcessor.Lock)
						{
							pendingHeight = TransactionJournal.NextScanHeight;
						}

						if (lastHeight == BitcoinStore.SmartHeaderChain.TipHeight && (pendingHeight is null || pendingHeight > BitcoinStore.SmartHeaderChain.TipHeight))
						{
							lock (Lock)
							{
								if (!request.Tcs.Task.IsCompleted)
								{
									request.Tcs.TrySetResult();
								}
								SynchronizationRequests.TryDequeue(out _, out _);
							}
							continue;
						}

						uint currentHeight = Math.Min((uint)lastHeight.Value + 1, pendingHeight ?? uint.MaxValue);
						bool backfill = currentHeight <= lastHeight.Value;

						FilterModel filter = await FilterIterator.GetAndRemoveAsync(currentHeight, cancellationToken).ConfigureAwait(false);
						var matchFound = await ProcessFilterModelAsync(filter, cancellationToken).ConfigureAwait(false);
						if (matchFound is null)
						{
							await FilterIterator.ClearAsync(cancellationToken).ConfigureAwait(false);
							SynchronizationRequestsSemaphore.Release(1);
							continue;
						}

						lock (TransactionProcessor.Lock)
						{
							reachedBlockChainTip = Math.Max((uint)lastHeight.Value, currentHeight) == BitcoinStore.SmartHeaderChain.TipHeight
								&& (TransactionJournal.NextScanHeight is not { } nextPending || nextPending > BitcoinStore.SmartHeaderChain.TipHeight);
							if (!backfill)
							{
								bool storeToDisk = matchFound.Value || reachedBlockChainTip;
								KeyManager.SetBestHeight(new Height(currentHeight), storeToDisk);
							}
						}
					}

					if (reachedBlockChainTip)
					{
						lock (Lock)
						{
							if (!request.Tcs.Task.IsCompleted)
							{
								request.Tcs.TrySetResult();
							}
							SynchronizationRequests.TryDequeue(out _, out _);
						}
					}
					else
					{
						SynchronizationRequestsSemaphore.Release(1);
					}
				}
				catch (Exception ex)
				{
					if (!request.Tcs.TrySetException(ex))
					{
						Logger.LogWarning($"Tried to set exception but status was already {request.Tcs.Task.Status} ({ex}).");
					}

					throw;
				}

				// Clear all caches once not needed.
				if (SynchronizationRequestsSemaphore.CurrentCount == 0)
				{
					await FilterIterator.ClearAsync(cancellationToken).ConfigureAwait(false);
				}
			}
		}
		catch (OperationCanceledException)
		{
			Logger.LogDebug("Filter processor's execution was stopped.");
		}
		catch (Exception ex)
		{
			Logger.LogError(ex);
			TerminateService.Instance?.SignalGracefulCrash(ex);
			throw;
		}
		finally
		{
			CancelPendingRequests(stopAcceptingRequests: false);
		}
	}

	private void CancelPendingRequests(bool stopAcceptingRequests)
	{
		lock (Lock)
		{
			RequestsStopped |= stopAcceptingRequests;
			while (SynchronizationRequests.TryDequeue(out var request, out _))
			{
				request.Tcs.TrySetCanceled();
			}
		}
	}

	/// <summary>
	/// Return the keys to test against the filter depending on the height of the filter and the type of synchronization.
	/// </summary>
	/// <param name="filterHeight">Height of the filter that needs to be tested.</param>
	/// <returns>Keys to test against this filter.</returns>
	private List<byte[]> GetScriptPubKeysToTest(Height filterHeight)
	{
		bool ScriptAlreadySpent(KeyManager.ScriptPubKeySpendingInfo spendingInfo) =>
			spendingInfo.LatestSpendingHeight is { } spendingHeight && spendingHeight < filterHeight;

		bool ScriptNotSpentAtTheMoment(KeyManager.ScriptPubKeySpendingInfo spendingInfo) =>
			!ScriptAlreadySpent(spendingInfo);

		var scriptsSpendingInfo = KeyManager.UnsafeGetSynchronizationInfos();
		var scriptPubKeyAccordingSyncType = scriptsSpendingInfo;

		return scriptPubKeyAccordingSyncType.Select(x => x.CompressedScriptPubKey).ToList();
	}

	private async Task<bool?> ProcessFilterModelAsync(FilterModel filter, CancellationToken cancel)
	{
		Block? currentBlock = null;
		WalletTransactionJournal.Conflict[] reorgedConflicts;
		lock (TransactionProcessor.Lock)
		{
			reorgedConflicts = TransactionJournal.Conflicts.Where(x => x.Height == filter.Header.Height && x.BlockHash != filter.Header.BlockHash.ToString()).ToArray();
		}
		if (reorgedConflicts.Length > 0)
		{
			currentBlock = await KeepTryingToGetBlockAsync(filter.Header.BlockHash, new Priority(filter.Header.Height), cancel).ConfigureAwait(false);
			var canonical = await BitcoinStore.IndexStore.FetchBatchAsync(filter.Header.Height, 1, cancel).ConfigureAwait(false);
			if (currentBlock.GetHash() != filter.Header.BlockHash || !currentBlock.Check() || canonical.Length == 0
				|| canonical[0].Header.Height != filter.Header.Height || canonical[0].Header.BlockHash != filter.Header.BlockHash)
			{
				return null;
			}
			foreach (var conflict in reorgedConflicts)
			{
				RestoreReorgedConflict(conflict);
			}
		}
		var height = new Height(filter.Header.Height);
		var toTestKeys = GetScriptPubKeysToTest(height);
		var matchFound = toTestKeys.Any() && FilterChecker.HasMatch(filter.Filter, filter.FilterKey, toTestKeys);
		uint256[] pending;
		lock (TransactionProcessor.Lock)
		{
			pending = TransactionJournal.GetPendingThrough(filter.Header.Height);
		}

		// A confirmed replacement can spend only foreign inputs and pay no wallet script. The ordinary receive filter cannot find it.
		if (matchFound || pending.Length > 0)
		{
			currentBlock ??= await KeepTryingToGetBlockAsync(filter.Header.BlockHash, new Priority(filter.Header.Height), cancel).ConfigureAwait(false);
			var canonical = await BitcoinStore.IndexStore.FetchBatchAsync(filter.Header.Height, 1, cancel).ConfigureAwait(false);
			if (currentBlock.GetHash() != filter.Header.BlockHash || !currentBlock.Check() || canonical.Length == 0 || canonical[0].Header.BlockHash != filter.Header.BlockHash)
			{
				return null;
			}

			var txsToProcess = new List<SmartTransaction>();
			for (int i = 0; i < currentBlock.Transactions.Count; i++)
			{
				Transaction tx = currentBlock.Transactions[i];
				txsToProcess.Add(new SmartTransaction(tx, height, currentBlock.GetHash(), i, firstSeen: currentBlock.Header.BlockTime, labels: BitcoinStore.MempoolService.TryGetLabel(tx.GetHash())));
			}
			lock (TransactionProcessor.Lock)
			{
				TransactionProcessor.Process(txsToProcess);
				TransactionJournal.CompleteScan(filter.Header.Height, pending, GetPendingTransactions());
			}
		}

		if (LastProcessedFilter is not { } last || last.Header.Height <= filter.Header.Height)
		{
			LastProcessedFilter = filter;
		}
		return matchFound;
	}

	private async void ReorgedAsync(object? sender, FilterModel invalidFilter)
	{
		try
		{
			uint256 invalidBlockHash = invalidFilter.Header.BlockHash;
			uint newBestHeight = invalidFilter.Header.Height == 0 ? 0 : invalidFilter.Header.Height - 1;

			using (await ReorgLock.LockAsync(CancellationToken.None).ConfigureAwait(false))
			{
				lock (TransactionProcessor.Lock)
				{
					KeyManager.SetMaxBestHeight(new Height(newBestHeight));
					TransactionProcessor.UndoBlock((int)invalidFilter.Header.Height);
					BitcoinStore.TransactionStore.ReleaseToMempoolFromBlock(invalidBlockHash);
					// Keep conflict proofs until an actual canonical block at the same height replaces them.
					// Removing a local filter because the backend is behind must not restore invalidated credit.
					foreach (var transaction in GetPendingTransactions().ToArray())
					{
						TransactionJournal.RewindPending(transaction, invalidFilter.Header.Height);
					}
					LastProcessedFilter = null;
				}
				await FilterIterator.RemoveNewerThanAsync(newBestHeight, CancellationToken.None).ConfigureAwait(false);
				await BlockDownloadService.RemoveBlocksAsync(invalidFilter.Header.Height).ConfigureAwait(false);
			}
		}
		catch (Exception ex)
		{
			Logger.LogWarning(ex);
		}
	}

	/// <summary>
	/// Attempt to get the bitcoin block from a full node as a primary source of data, or use P2P as a fallback.
	/// </summary>
	private async Task<Block> KeepTryingToGetBlockAsync(uint256 blockHash, Priority priority, CancellationToken cancellationToken)
	{
		ISourceRequest[] sourceRequests = [TrustedFullNodeSourceRequest.Instance, P2pSourceRequest.Automatic];
		while (true)
		{
			foreach (ISourceRequest sourceRequest in sourceRequests)
			{
				BlockDownloadService.IResult result = await BlockDownloadService.TryGetBlockAsync(sourceRequest, blockHash, priority, cancellationToken)
					.ConfigureAwait(false);

				switch (result)
				{
					case BlockDownloadService.SuccessResult successFullNodeResult:
						return successFullNodeResult.Block;

					case BlockDownloadService.CanceledResult:
						throw new OperationCanceledException();
				}
			}
		}
	}

	public override async Task StartAsync(CancellationToken cancellationToken)
	{
		// Wallet.Initialize has now asserted the selected network, including for imported watch-only wallets.
		lock (TransactionProcessor.Lock)
		{
			_ = TransactionJournal;
		}
		lock (Lock)
		{
			RequestsStopped = false;
		}
		BitcoinStore.IndexStore.Reorged += ReorgedAsync;
		TransactionProcessor.ConfirmedConflictDetected += TransactionJournal.RecordConflict;
		TransactionProcessor.UnconfirmedTransactionDetected += UnconfirmedTransactionDetected;
		TransactionProcessor.IsInvalidatedTransaction = TransactionJournal.IsInvalidated;
		await BlockDownloadService.StartAsync(cancellationToken).ConfigureAwait(false);
		await base.StartAsync(cancellationToken).ConfigureAwait(false);
	}

	public override async Task StopAsync(CancellationToken cancellationToken)
	{
		BitcoinStore.IndexStore.Reorged -= ReorgedAsync;
		try
		{
			await base.StopAsync(cancellationToken).ConfigureAwait(false);
			await BlockDownloadService.StopAsync(cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			TransactionProcessor.ConfirmedConflictDetected -= TransactionJournal.RecordConflict;
			TransactionProcessor.UnconfirmedTransactionDetected -= UnconfirmedTransactionDetected;
			TransactionProcessor.IsInvalidatedTransaction = null;
			// ExecuteAsync may never have started when startup was cancelled.
			CancelPendingRequests(stopAcceptingRequests: true);
		}
	}

	public override void Dispose()
	{
		BitcoinStore.IndexStore.Reorged -= ReorgedAsync;
		TransactionProcessor.ConfirmedConflictDetected -= TransactionJournal.RecordConflict;
		TransactionProcessor.UnconfirmedTransactionDetected -= UnconfirmedTransactionDetected;
		TransactionProcessor.IsInvalidatedTransaction = null;
		base.Dispose();
		CancelPendingRequests(stopAcceptingRequests: true);
		if (ExecuteTask is { IsCompleted: false } executeTask)
		{
			// The worker may still be using the semaphore while cancellation unwinds.
			_ = executeTask.ContinueWith(
				_ => SynchronizationRequestsSemaphore.Dispose(),
				CancellationToken.None,
				TaskContinuationOptions.ExecuteSynchronously,
				TaskScheduler.Default);
		}
		else
		{
			SynchronizationRequestsSemaphore.Dispose();
		}
	}

	public record SyncRequest(TaskCompletionSource Tcs);
}
