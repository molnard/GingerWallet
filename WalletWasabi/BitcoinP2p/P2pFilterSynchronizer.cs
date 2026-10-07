using Microsoft.Extensions.Hosting;
using NBitcoin;
using NBitcoin.Crypto;
using NBitcoin.Protocol;
using NBitcoin.Protocol.Behaviors;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Backend.Models;
using WalletWasabi.Blockchain.BlockFilters;
using WalletWasabi.Blockchain.Blocks;
using WalletWasabi.Logging;
using WalletWasabi.Stores;

namespace WalletWasabi.BitcoinP2p;

/// <summary>Validates BIP157 responses against proof-of-work headers, checkpoints and independent peers.</summary>
public sealed class P2pFilterSynchronizer : BackgroundService
{
	private readonly P2pNetwork _p2p;
	private readonly BitcoinStore _store;
	private readonly Network _network;
	private readonly ConcurrentChain _headers;
	private readonly string _headersPath;
	private readonly Bip158Checkpoints.Checkpoint[] _checkpoints;
	private readonly SortedDictionary<uint, uint256> _filterHeaders = new();
	private volatile uint _verifiedHeight;
	private uint256 _verifiedHeader = uint256.Zero;
	private DateTimeOffset _lastSave;
	internal uint VerifiedHeight => _verifiedHeight;

	public P2pFilterSynchronizer(P2pNetwork p2p, BitcoinStore store, string workDirectory, Network network)
	{
		_p2p = p2p;
		_store = store;
		_network = network;
		_headersPath = Path.Combine(workDirectory, "BlockHeaders.dat");
		_headers = new ConcurrentChain(network);
		_checkpoints = Bip158Checkpoints.ForNetwork(network);
		ResetFilterHeaders();
		if (File.Exists(_headersPath))
		{
			try
			{
				_headers.Load(File.ReadAllBytes(_headersPath), network);
				Bip158Checkpoints.CheckBlockAnchors(_headers, network);
				if (_headers.GetBlock(0)?.HashBlock != network.GenesisHash || !_headers.Tip.CheckProofOfWorkAndTarget(network))
				{
					throw new InvalidOperationException("Invalid cached block headers.");
				}
			}
			catch (Exception ex) when (ex is IOException or FormatException or InvalidOperationException or ArgumentException)
			{
				Logger.LogWarning($"Cannot load P2P block headers: {ex.Message}");
				_headers.SetTip(new ChainedBlock(network.GetGenesis().Header, 0));
			}
		}
		p2p.Nodes.NodeConnectionParameters.TemplateBehaviors.Add(new FilterChainBehavior(_headers)
		{
			StripHeader = false,
			CanRespondToGetHeaders = false
		});
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		await _store.IndexStore.InitializedTcs.Task.WaitAsync(stoppingToken).ConfigureAwait(false);
		while (!stoppingToken.IsCancellationRequested)
		{
			try
			{
				if (await SynchronizeOnceAsync(stoppingToken).ConfigureAwait(false))
				{
					continue;
				}
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
			{
				break;
			}
			catch (Exception ex)
			{
				Logger.LogWarning($"P2P compact filter synchronization will retry: {ex.Message}");
			}
			await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
		}
	}

	public override async Task StopAsync(CancellationToken cancellationToken)
	{
		await base.StopAsync(cancellationToken).ConfigureAwait(false);
		SaveHeaders();
	}

	private async Task<bool> SynchronizeOnceAsync(CancellationToken cancellationToken)
	{
		var peers = _p2p.Nodes.ConnectedNodes
			.Where(x => x.State == NodeState.HandShaked && x.PeerVersion.Services.HasFlag(NodeServices.NODE_COMPACT_FILTERS))
			.GroupBy(x => NetworkGroup(x.Peer.Endpoint)).Select(x => x.First()).Take(_network == Network.RegTest ? 1 : 2).ToArray();
		if (peers.Length < (_network == Network.RegTest ? 1 : 2))
		{
			return false;
		}
		var tip = _store.SmartHeaderChain;
		tip.SetServerTipHeight((uint)Math.Max(_headers.Height, peers.Max(x => x.PeerVersion.StartHeight)));
		if (_headers.Height < _checkpoints[0].Height || (_headers.Height < tip.TipHeight && peers.Any(x => x.Behaviors.Find<FilterChainBehavior>()?.IsCaughtUp != true)))
		{
			return false; // Header synchronization may still be catching up with the existing cache.
		}
		Bip158Checkpoints.CheckBlockAnchors(_headers, _network);
		if (DateTimeOffset.UtcNow - _lastSave > TimeSpan.FromMinutes(1))
		{
			SaveHeaders();
		}

		// Check hashes even at the same height: a reorganization need not raise the chain tip.
		while (_headers.GetBlock((int)tip.TipHeight)?.HashBlock != tip.TipHash)
		{
			if (tip.TipHeight <= _checkpoints[0].Height)
			{
				throw new InvalidOperationException("Reorganization crosses the trusted starting checkpoint.");
			}
			await _store.IndexStore.TryRemoveLastFilterAsync().ConfigureAwait(false);
		}
		if (_verifiedHeight > tip.TipHeight)
		{
			if (_filterHeaders.TryGetValue(tip.TipHeight, out var header))
			{
				_verifiedHeight = tip.TipHeight;
				_verifiedHeader = header;
				foreach (var height in _filterHeaders.Keys.Where(x => x > _verifiedHeight).ToArray())
				{
					_filterHeaders.Remove(height);
				}
			}
			else
			{
				ResetFilterHeaders();
			}
		}
		if (_verifiedHeight >= _headers.Height)
		{
			return false;
		}

		// On restart, reconstruct authenticated filter headers without downloading cached filter bodies.
		bool recovering = _verifiedHeight < tip.TipHeight;
		uint start = _verifiedHeight + 1;
		uint stop = Math.Min(start + (recovering ? 1999u : 499u), recovering ? tip.TipHeight : (uint)_headers.Height);
		var stopBlock = _headers.GetBlock((int)stop)!;
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(TimeSpan.FromSeconds(90));
		CompactFilterHeadersPayload[] responses;
		try
		{
			responses = await Task.WhenAll(peers.Select(peer => RequestHeadersAsync(peer, start, stopBlock.HashBlock, timeout.Token))).ConfigureAwait(false);
			ValidateHeaders(responses[0], responses[^1], stopBlock.HashBlock, checked((int)(stop - start + 1)), _verifiedHeader);
		}
		catch
		{
			foreach (var peer in peers)
			{
				peer.DisconnectAsync("Compact filter headers unavailable or inconsistent");
			}
			throw;
		}
		var hashes = responses[0].FilterHeaders;
		var authenticated = new uint256[hashes.Count];
		var previous = _verifiedHeader;
		for (int i = 0; i < hashes.Count; i++)
		{
			previous = Hashes.DoubleSHA256(hashes[i].ToBytes().Concat(previous.ToBytes()).ToArray());
			authenticated[i] = previous;
			var checkpoint = _checkpoints.FirstOrDefault(x => x.Height == start + i);
			if (checkpoint is not null && checkpoint.FilterHeader != previous)
			{
				foreach (var peer in peers)
				{
					peer.DisconnectAsync("Compact filter checkpoint mismatch");
				}
				throw new InvalidOperationException($"Filter headers do not match checkpoint {checkpoint.Height}.");
			}
		}
		if (!recovering)
		{
			FilterModel[] filters;
			try
			{
				filters = await RequestFiltersAsync(peers[0], start, stopBlock.HashBlock, hashes, timeout.Token).ConfigureAwait(false);
			}
			catch
			{
				peers[0].DisconnectAsync("Invalid or incomplete compact filters");
				throw;
			}
			if (_headers.GetBlock((int)stop)?.HashBlock != stopBlock.HashBlock || tip.TipHeight + 1 != start || tip.TipHash != filters[0].Header.PrevHash)
			{
				return true; // A header reorganization happened while the responses were in flight.
			}
			await _store.IndexStore.AddNewFiltersAsync(filters).ConfigureAwait(false);
		}
		else
		{
			var stored = await _store.IndexStore.FetchBatchAsync(start, hashes.Count, timeout.Token).ConfigureAwait(false);
			for (int i = 0; i < hashes.Count; i++)
			{
				uint height = start + (uint)i;
				if (i >= stored.Length || stored[i].Header.Height != height || stored[i].Header.BlockHash != _headers.GetBlock((int)height)?.HashBlock || Hashes.DoubleSHA256(stored[i].Filter.ToBytes()) != hashes[i])
				{
					Logger.LogWarning($"Replacing corrupt compact filter cache from height {height}.");
					await _store.IndexStore.RemoveAllNewerThanAsync(height - 1).ConfigureAwait(false);
					ResetFilterHeaders();
					return true;
				}
			}
		}
		if (_headers.GetBlock((int)stop)?.HashBlock != stopBlock.HashBlock)
		{
			ResetFilterHeaders();
			return true;
		}
		for (int i = 0; i < authenticated.Length; i++)
		{
			_filterHeaders[start + (uint)i] = authenticated[i];
		}
		_verifiedHeight = stop;
		_verifiedHeader = previous;
		while (_filterHeaders.Count > 20_000)
		{
			_filterHeaders.Remove(_filterHeaders.Keys.First());
		}
		return true;
	}

	internal static void ValidateHeaders(CompactFilterHeadersPayload first, CompactFilterHeadersPayload second, uint256 stopHash, int count, uint256 previousHeader)
	{
		if (first.FilterType != FilterType.Basic || second.FilterType != FilterType.Basic || first.StopHash != stopHash || second.StopHash != stopHash ||
			first.FilterHeaders.Count != count || second.FilterHeaders.Count != count || first.PreviousFilterHeader != previousHeader || second.PreviousFilterHeader != previousHeader ||
			!first.FilterHeaders.SequenceEqual(second.FilterHeaders))
		{
			throw new InvalidOperationException("Compact filter headers do not agree with the requested range and peer.");
		}
	}

	private static Task<CompactFilterHeadersPayload> RequestHeadersAsync(Node peer, uint start, uint256 stop, CancellationToken cancellationToken) => Task.Run(() =>
	{
		using var listener = peer.CreateListener().OfType<CompactFilterHeadersPayload>();
		peer.SendMessage(new GetCompactFilterHeadersPayload(FilterType.Basic, start, stop), cancellationToken);
		return listener.ReceivePayload<CompactFilterHeadersPayload>(cancellationToken);
	}, cancellationToken);

	private Task<FilterModel[]> RequestFiltersAsync(Node peer, uint start, uint256 stop, IReadOnlyList<uint256> hashes, CancellationToken cancellationToken) => Task.Run(() =>
	{
		using var listener = peer.CreateListener().OfType<CompactFilterPayload>();
		peer.SendMessage(new GetCompactFiltersPayload(FilterType.Basic, start, stop), cancellationToken);
		var filters = new FilterModel[hashes.Count];
		for (int i = 0; i < filters.Length; i++)
		{
			var payload = listener.ReceivePayload<CompactFilterPayload>(cancellationToken);
			var block = _headers.GetBlock(checked((int)start + i)) ?? throw new InvalidOperationException("Missing block header.");
			filters[i] = ValidateFilter(payload, block, hashes[i]);
		}
		return filters;
	}, cancellationToken);

	internal static FilterModel ValidateFilter(CompactFilterPayload payload, ChainedBlock block, uint256 expectedHash)
	{
		if (payload.FilterType != FilterType.Basic || payload.BlockHash != block.HashBlock || payload.FilterBytes.Length > 1_000_000 || Hashes.DoubleSHA256(payload.FilterBytes) != expectedHash)
		{
			throw new InvalidOperationException("Compact filter does not match its authenticated block and hash.");
		}
		var filter = new GolombRiceFilter(payload.FilterBytes, 19, 784931);
		// Decode the complete stream now, before storage; malformed filters must not stall wallet scanning.
		if (filter.N > 1_000_000 || (filter.N == 0 && filter.Data.Length != 0))
		{
			throw new InvalidOperationException("Compact filter entry count or empty encoding is invalid.");
		}
		var reader = filter.GetNewGRStreamReader();
		int decoded = 0;
		ulong previousValue = 0;
		while (reader.TryRead(out var value))
		{
			decoded++;
			if (decoded > filter.N || value < previousValue || value >= (ulong)filter.N * filter.M)
			{
				throw new InvalidOperationException("Compact filter values are outside their valid range.");
			}
			previousValue = value;
		}
		if (decoded != filter.N)
		{
			throw new InvalidOperationException("Truncated compact filter.");
		}
		return new(new SmartHeader(block.HashBlock, block.Header.HashPrevBlock, (uint)block.Height, block.Header.BlockTime), filter);
	}

	internal static string NetworkGroup(EndPoint endpoint)
	{
		if (endpoint is IPEndPoint ip)
		{
			var address = ip.Address.IsIPv4MappedToIPv6 ? ip.Address.MapToIPv4() : ip.Address;
			var bytes = address.GetAddressBytes();
			return Convert.ToHexString(bytes[..(address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 2 : 4)]);
		}
		return endpoint is DnsEndPoint dns ? dns.Host.ToLowerInvariant() : endpoint.ToString() ?? "";
	}

	private sealed class FilterChainBehavior(ConcurrentChain chain) : ChainBehavior(chain)
	{
		private volatile bool _isCaughtUp;
		public bool IsCaughtUp => _isCaughtUp;

		protected override void AttachCore()
		{
			base.AttachCore();
			AttachedNode.MessageReceived += OnMessageReceived;
		}

		protected override void DetachCore()
		{
			AttachedNode.MessageReceived -= OnMessageReceived;
			base.DetachCore();
		}

		private void OnMessageReceived(Node node, IncomingMessage message)
		{
			if (message.Message.Payload is HeadersPayload headers)
			{
				_isCaughtUp = headers.Headers.Count < 2000 && !InvalidHeaderReceived;
			}
		}

		public override object Clone() => new FilterChainBehavior(Chain) { StripHeader = false, CanRespondToGetHeaders = false };
	}

	private void ResetFilterHeaders()
	{
		_verifiedHeight = _checkpoints[0].Height;
		_verifiedHeader = _checkpoints[0].FilterHeader;
		_filterHeaders.Clear();
		_filterHeaders[_verifiedHeight] = _verifiedHeader;
	}

	private void SaveHeaders()
	{
		Directory.CreateDirectory(Path.GetDirectoryName(_headersPath)!);
		var temporary = _headersPath + ".tmp";
		File.WriteAllBytes(temporary, _headers.Clone().ToBytes());
		File.Move(temporary, _headersPath, overwrite: true);
		_lastSave = DateTimeOffset.UtcNow;
	}
}
