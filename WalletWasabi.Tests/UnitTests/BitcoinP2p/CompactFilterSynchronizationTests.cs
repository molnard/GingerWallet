using NBitcoin;
using NBitcoin.Crypto;
using NBitcoin.Protocol;
using NBitcoin.Protocol.Behaviors;
using Moq;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Backend.Models;
using WalletWasabi.BitcoinCore.Endpointing;
using WalletWasabi.Filter;
using WalletWasabi.BitcoinP2p;
using WalletWasabi.Blockchain.BlockFilters;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Blockchain.Blocks;
using WalletWasabi.Blockchain.Mempool;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Helpers;
using WalletWasabi.Models;
using WalletWasabi.Stores;
using WalletWasabi.Wallets;
using WalletWasabi.Tests.TestCommon;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.BitcoinP2p;

public class CompactFilterSynchronizationTests
{
	[Fact]
	public async Task DownloadsAuthenticatedFiltersOverTheP2pWire()
	{
		var directory = Path.Combine(TestDirectory.Get(), Guid.NewGuid().ToString("N"));
		var network = Network.RegTest;
		var serverChain = new ConcurrentChain(network);
		var block = network.Consensus.ConsensusFactory.CreateBlock();
		var coinbase = network.CreateTransaction();
		coinbase.Inputs.Add(new TxIn(new OutPoint(uint256.Zero, uint.MaxValue), new Script(Op.GetPushOp(1))));
		using var key = new Key();
		coinbase.Outputs.Add(Money.Coins(50), key.PubKey.WitHash.ScriptPubKey);
		block.Transactions.Add(coinbase);
		block.UpdateMerkleRoot();
		block.Header.HashPrevBlock = network.GenesisHash;
		block.Header.BlockTime = network.GetGenesis().Header.BlockTime.AddSeconds(1);
		block.Header.Bits = network.GetGenesis().Header.Bits;
		while (!block.Header.CheckProofOfWork())
		{
			block.Header.Nonce++;
		}
		serverChain.SetTip(new ChainedBlock(block.Header, block.GetHash(), serverChain.Tip));
		var filter = GolombRiceFilterBuilder.BuildBasicFilter(block);
		var filtersByHash = new Dictionary<uint256, GolombRiceFilter> { [block.GetHash()] = filter };
		int bodyRequests = 0;
		using var server = new NodeServer(network)
		{
			AllowLocalPeers = true,
			LocalEndpoint = new IPEndPoint(IPAddress.Loopback, PortFinder.GetRandomPorts(1)[0])
		};
		server.InboundNodeConnectionParameters.Services = NodeServices.NODE_WITNESS | NodeServices.NODE_COMPACT_FILTERS;
		server.InboundNodeConnectionParameters.TemplateBehaviors.Add(new ChainBehavior(serverChain) { AutoSync = false });
		server.MessageReceived += (_, message) =>
		{
			if (message.Message.Payload is GetCompactFilterHeadersPayload query)
			{
				var previousHeader = Bip158Checkpoints.ForNetwork(network).Single().FilterHeader;
				for (int height = 1; height < query.StartHeight; height++)
				{
					previousHeader = filtersByHash[serverChain.GetBlock(height).HashBlock].GetHeader(previousHeader);
				}
				var stop = serverChain.GetBlock(query.StopHash)!.Height;
				message.Node.SendMessage(new CompactFilterHeadersPayload
				{
					StopHash = query.StopHash,
					PreviousFilterHeader = previousHeader,
					FilterHeaders = Enumerable.Range((int)query.StartHeight, stop - (int)query.StartHeight + 1)
						.Select(height => Hashes.DoubleSHA256(filtersByHash[serverChain.GetBlock(height).HashBlock].ToBytes())).ToList()
				});
			}
			else if (message.Message.Payload is GetCompactFiltersPayload filterQuery)
			{
				Interlocked.Increment(ref bodyRequests);
				var stop = serverChain.GetBlock(filterQuery.StopHash)!.Height;
				for (int height = (int)filterQuery.StartHeight; height <= stop; height++)
				{
					var hash = serverChain.GetBlock(height).HashBlock;
					message.Node.SendMessage(new CompactFilterPayload(FilterType.Basic, hash, filtersByHash[hash].ToBytes()));
				}
			}
		};
		server.Listen();
		var chain = new SmartHeaderChain();
		await using var index = new IndexStore(directory, network, chain, useBip158: true);
		await using var transactions = new AllTransactionStore(directory, network);
		var store = new BitcoinStore(index, transactions, new MempoolService(), chain, Mock.Of<IFileSystemBlockRepository>());
		await index.InitializeAsync(CancellationToken.None);
		using var p2p = new P2pNetwork(network, server.LocalEndpoint, null, directory, store);
		using var synchronizer = new P2pFilterSynchronizer(p2p, store, directory, network);
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		try
		{
			await p2p.StartAsync(timeout.Token);
			await synchronizer.StartAsync(timeout.Token);
			while (chain.TipHeight != 1)
			{
				await Task.Delay(50, timeout.Token);
			}
			var stored = (await index.FetchBatchAsync(1, 1, timeout.Token)).Single();
			Assert.True(stored.IsBip158);
			Assert.Equal(block.GetHash(), stored.Header.BlockHash);
			Assert.Equal(network.GenesisHash, stored.Header.PrevHash);
			Assert.Equal(filter.ToBytes(), stored.Filter.ToBytes());

			int reorgs = 0;
			index.Reorged += (_, _) => Interlocked.Increment(ref reorgs);
			var fork1 = MineHeader(new ChainedBlock(network.GetGenesis().Header, 0), 123);
			var fork2 = MineHeader(fork1, 124);
			filtersByHash[fork1.HashBlock] = new GolombRiceFilter([0], 19, 784931);
			filtersByHash[fork2.HashBlock] = new GolombRiceFilter([0], 19, 784931);
			serverChain.SetTip(fork2);
			foreach (var node in server.ConnectedNodes)
			{
				node.SendMessage(new InvPayload(new InventoryVector(InventoryType.MSG_BLOCK, fork2.HashBlock)));
			}
			while (chain.TipHash != fork2.HashBlock)
			{
				await Task.Delay(50, timeout.Token);
			}
			var replacement = await index.FetchBatchAsync(1, 2, timeout.Token);
			Assert.Equal(1, reorgs);
			Assert.Equal(fork1.HashBlock, replacement[0].Header.BlockHash);
			Assert.Equal(fork1.HashBlock, replacement[1].Header.PrevHash);
		}
		finally
		{
			await synchronizer.StopAsync(CancellationToken.None);
			await p2p.StopAsync(CancellationToken.None);
		}
		int previousBodyRequests = bodyRequests;
		using var restartedP2p = new P2pNetwork(network, server.LocalEndpoint, null, directory, store);
		using var restartedSync = new P2pFilterSynchronizer(restartedP2p, store, directory, network);
		try
		{
			await restartedP2p.StartAsync(timeout.Token);
			await restartedSync.StartAsync(timeout.Token);
			while (restartedSync.VerifiedHeight != 2)
			{
				await Task.Delay(50, timeout.Token);
			}
			Assert.Equal(previousBodyRequests, bodyRequests);
		}
		finally
		{
			await restartedSync.StopAsync(CancellationToken.None);
			await restartedP2p.StopAsync(CancellationToken.None);
		}

		// Corruption below the cached tip must be repaired through the wallet's existing reorg path.
		var cached = await index.FetchBatchAsync(1, 2, timeout.Token);
		await index.RemoveAllNewerThanAsync(0);
		var damaged = new GolombRiceFilterBuilder().SetKey(cached[0].Header.BlockHash).SetP(19).SetM(784931).AddEntries([key.PubKey.ScriptPubKey.ToBytes()]).Build();
		await index.AddNewFiltersAsync([new FilterModel(cached[0].Header, damaged), cached[1]]);
		using var repairP2p = new P2pNetwork(network, server.LocalEndpoint, null, directory, store);
		using var repairSync = new P2pFilterSynchronizer(repairP2p, store, directory, network);
		try
		{
			await repairP2p.StartAsync(timeout.Token);
			await repairSync.StartAsync(timeout.Token);
			while (repairSync.VerifiedHeight != 2)
			{
				await Task.Delay(50, timeout.Token);
			}
			Assert.True(bodyRequests > previousBodyRequests);
			Assert.Equal(filtersByHash[cached[0].Header.BlockHash].ToBytes(), (await index.FetchBatchAsync(1, 1, timeout.Token)).Single().Filter.ToBytes());
		}
		finally
		{
			await repairSync.StopAsync(CancellationToken.None);
			await repairP2p.StopAsync(CancellationToken.None);
		}
	}

	private static ChainedBlock MineHeader(ChainedBlock previous, uint merkleRoot)
	{
		var header = Network.RegTest.Consensus.ConsensusFactory.CreateBlockHeader();
		header.HashPrevBlock = previous.HashBlock;
		header.HashMerkleRoot = new uint256(merkleRoot);
		header.BlockTime = previous.Header.BlockTime.AddSeconds(1);
		header.Bits = previous.Header.Bits;
		while (!header.CheckProofOfWork())
		{
			header.Nonce++;
		}
		return new(header, header.GetHash(), previous);
	}

	[Fact]
	public void AuthenticatedFilterMatchesBothWalletScriptTypesAfterStorage()
	{
		var manager = KeyManager.CreateNew(out _, "", Network.RegTest);
		var scripts = manager.UnsafeGetSynchronizationInfos(useBip158: true).Select(x => x.CompressedScriptPubKey).ToArray();
		var genesis = Network.RegTest.GetGenesis();
		var filter = new GolombRiceFilterBuilder().SetKey(genesis.GetHash()).SetP(19).SetM(784931).AddEntries(scripts).Build();
		var payload = new CompactFilterPayload(FilterType.Basic, genesis.GetHash(), filter.ToBytes());
		var validated = P2pFilterSynchronizer.ValidateFilter(payload, new ChainedBlock(genesis.Header, 0), Hashes.DoubleSHA256(filter.ToBytes()));
		using var storage = BlockFilterSqliteStorage.FromFile(SqliteStorageHelper.InMemoryDatabase, validated, useBip158: true);
		var stored = storage.Fetch(0, 1).Single();
		Assert.True(stored.IsBip158);
		Assert.Equal((byte)19, stored.Filter.P);
		Assert.Equal(784931u, stored.Filter.M);
		Assert.All(scripts, script => Assert.True(FilterChecker.HasMatch(stored.Filter, stored.FilterKey, [script])));
		Assert.Equal(validated.Header.PrevHash, stored.Header.PrevHash);
	}

	[Theory]
	[InlineData(0)]
	[InlineData(1)]
	[InlineData(2)]
	[InlineData(3)]
	[InlineData(4)]
	[InlineData(5)]
	public void RejectsInvalidFilterBodies(int attack)
	{
		var genesis = Network.RegTest.GetGenesis();
		var original = GolombRiceFilterBuilder.BuildBasicFilter(genesis).ToBytes();
		var bytes = attack == 2 ? new byte[] { 1 } : attack == 3 ? new byte[1_000_001] : attack == 4 ? new byte[] { 0, 0 } : original;
		Assert.ThrowsAny<Exception>(() =>
		{
			var payload = new CompactFilterPayload(attack == 0 ? (FilterType)1 : FilterType.Basic, attack == 1 ? uint256.One : genesis.GetHash(), bytes);
			P2pFilterSynchronizer.ValidateFilter(payload, new ChainedBlock(genesis.Header, 0), attack == 5 ? uint256.One : Hashes.DoubleSHA256(bytes));
		});
	}

	[Fact]
	public void RequiresIndependentPeersToAgreeWithTheEntireHeaderRange()
	{
		var previous = new uint256(7);
		var stop = new uint256(8);
		var first = new CompactFilterHeadersPayload { StopHash = stop, PreviousFilterHeader = previous, FilterHeaders = [uint256.One, new uint256(2)] };
		var second = new CompactFilterHeadersPayload { StopHash = stop, PreviousFilterHeader = previous, FilterHeaders = [uint256.One, new uint256(2)] };
		P2pFilterSynchronizer.ValidateHeaders(first, second, stop, 2, previous);
		second.FilterHeaders.Reverse();
		Assert.Throws<InvalidOperationException>(() => P2pFilterSynchronizer.ValidateHeaders(first, second, stop, 2, previous));
		second.FilterHeaders.Reverse();
		Assert.Throws<InvalidOperationException>(() => P2pFilterSynchronizer.ValidateHeaders(first, second, stop, 3, previous));
		Assert.Throws<InvalidOperationException>(() => P2pFilterSynchronizer.ValidateHeaders(first, second, uint256.Zero, 2, previous));
		Assert.Throws<InvalidOperationException>(() => P2pFilterSynchronizer.ValidateHeaders(first, second, stop, 2, uint256.Zero));
	}

	[Fact]
	public void GroupsPeersByNetworkRatherThanPort()
	{
		Assert.Equal(P2pFilterSynchronizer.NetworkGroup(new IPEndPoint(IPAddress.Parse("10.20.30.40"), 1)), P2pFilterSynchronizer.NetworkGroup(new IPEndPoint(IPAddress.Parse("10.20.50.60"), 2)));
		Assert.NotEqual(P2pFilterSynchronizer.NetworkGroup(new IPEndPoint(IPAddress.Parse("10.20.30.40"), 1)), P2pFilterSynchronizer.NetworkGroup(new IPEndPoint(IPAddress.Parse("10.21.30.40"), 1)));
		Assert.Equal(P2pFilterSynchronizer.NetworkGroup(new IPEndPoint(IPAddress.Parse("10.20.30.40"), 1)), P2pFilterSynchronizer.NetworkGroup(new IPEndPoint(IPAddress.Parse("::ffff:10.20.30.40"), 1)));
	}

	[Fact]
	public async Task BackendAndBasicCachesStaySeparate()
	{
		var directory = Path.Combine(TestDirectory.Get(), Guid.NewGuid().ToString("N"));
		await using var legacy = new IndexStore(directory, Network.Main, new SmartHeaderChain());
		await legacy.InitializeAsync(CancellationToken.None);
		var original = (await legacy.FetchBatchAsync(481824, 1, CancellationToken.None)).Single();
		await using var basic = new IndexStore(directory, Network.Main, new SmartHeaderChain(), useBip158: true);
		await basic.InitializeAsync(CancellationToken.None);
		Assert.True(File.Exists(Path.Combine(directory, "IndexStore.sqlite")));
		Assert.True(File.Exists(Path.Combine(directory, "IndexStore.Bip158.sqlite")));
		Assert.True((await basic.FetchBatchAsync(481824, 1, CancellationToken.None)).Single().IsBip158);
		var retained = (await legacy.FetchBatchAsync(481824, 1, CancellationToken.None)).Single();
		Assert.False(retained.IsBip158);
		Assert.Equal(original.Filter.ToBytes(), retained.Filter.ToBytes());
	}

	[Fact]
	public async Task BasicCacheRollsBackPastTheInMemoryHeaderWindow()
	{
		var directory = Path.Combine(TestDirectory.Get(), Guid.NewGuid().ToString("N"));
		var start = Bip158Checkpoints.StartingFilter(Network.Main);
		var previous = start.Header.BlockHash;
		var filters = Enumerable.Range(1, 6000).Select(offset =>
		{
			var hash = new uint256((ulong)offset);
			var filter = new FilterModel(new SmartHeader(hash, previous, start.Header.Height + (uint)offset, start.Header.BlockTime), new GolombRiceFilter([0], 19, 784931));
			previous = hash;
			return filter;
		}).ToArray();
		await using (var original = new IndexStore(directory, Network.Main, new SmartHeaderChain(), useBip158: true))
		{
			await original.InitializeAsync(CancellationToken.None);
			await original.AddNewFiltersAsync(filters);
		}
		var chain = new SmartHeaderChain();
		await using var reopened = new IndexStore(directory, Network.Main, chain, useBip158: true);
		await reopened.InitializeAsync(CancellationToken.None);
		Assert.Equal(5000, chain.HashCount);
		await reopened.RemoveAllNewerThanAsync(start.Header.Height);
		Assert.Equal(start.Header.BlockHash, chain.TipHash);
		Assert.Single(await reopened.FetchBatchAsync(start.Header.Height, 6001, CancellationToken.None));
	}

	[Fact]
	public void BirthdayPersistsButRecoveryAndExplicitRescanDoNotSkipHistory()
	{
		var path = Path.Combine(TestDirectory.Get(), "birthday-wallet.json");
		var manager = KeyManager.CreateNew(out var mnemonic, "", Network.Main, path);
		Assert.Equal(Bip158Checkpoints.NewWalletBirthday(Network.Main), manager.BirthHeight);
		Assert.Equal(manager.BirthHeight, KeyManager.FromFile(path).BirthHeight);
		Assert.Null(KeyManager.Recover(mnemonic, "", Network.Main, KeyManager.GetAccountKeyPath(Network.Main, ScriptPubKeyType.Segwit)).BirthHeight);
		manager.SetBestHeight(new Height(1_000_000));
		manager.SetResyncParameters(new Height(500_000), manager.MinGapLimit);
		Assert.Null(KeyManager.FromFile(path).BirthHeight);
		Assert.DoesNotContain("BirthHeight", File.ReadAllText(path));
		Assert.Equal(new Height(499_999), KeyManager.FromFile(path).GetBestHeight());
	}

	[Fact]
	public void RegtestGenesisFilterHeaderMatchesPublishedCheckpoint()
	{
		Assert.Equal(new uint256("485e301e4509d7f0d954bf5b529f3ecef68c5191fd0e635f775c1d0266dc5a2b"), Bip158Checkpoints.ForNetwork(Network.RegTest).Single().FilterHeader);
	}
}
