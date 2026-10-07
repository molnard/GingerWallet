using NBitcoin;
using System.Linq;
using WalletWasabi.Backend.Models;
using WalletWasabi.Blockchain.Blocks;

namespace WalletWasabi.Blockchain.BlockFilters;

/// <summary>Bitcoin block and BIP158 filter-header anchors from Wasabi v2.8.3.</summary>
public static class Bip158Checkpoints
{
	public record Checkpoint(uint Height, uint256 BlockHash, uint256 FilterHeader, long BlockTime);

	private static readonly Checkpoint[] Mainnet =
	[
		new(481824, new uint256("0000000000000000001c8018d9cb3b742ef25114f27563e3fc4a1902167f9893"), new uint256("8950b517b9246048f9fd27adeda6802e5b6d08bc7a94f628619c2d4dc4bb4d67"), 1503539857),
		new(500000, new uint256("00000000000000000024fb37364cbf81fd49cc2d51c09c75c35433c3a1945d04"), new uint256("5d16ca293c9bdc0a9bc279b63f99fb661be38b095a59a44200a807caaa631a3c"), 1513622125),
		new(550000, new uint256("000000000000000000223b7a2298fb1c6c75fb0efc28a4c56853ff4112ec6bc9"), new uint256("594610c31fc6bd749d4c3b8d013f805ceea5b3c3ea4528f15f92038cda23c08b"), 1542162941),
		new(600000, new uint256("00000000000000000007316856900e76b4f7a9139cfbfba89842c8d196cd5f91"), new uint256("bde0854d0b2f4386a860462547140e0c6817f5b4b2ab515ef70e204e377598f8"), 1571443461),
		new(650000, new uint256("0000000000000000000060e32d547b6ae2ded52aadbc6310808e4ae42b08cc6a"), new uint256("7f302e88b0ae8598ade5773303b91d7d93bac88bccbf70c2c858972fb28d744d"), 1601077165),
		new(700000, new uint256("0000000000000000000590fc0f3eba193a278534220b2b37e9849e1a770ca959"), new uint256("de6674428d21ebe39fcb97071ded72fff2158ba59c06bab12ee99dec1e3b5542"), 1631333672),
		new(750000, new uint256("0000000000000000000592a974b1b9f087cb77628bb4a097d5c2c11b3476a58e"), new uint256("457f9365c4d98a5cd60a7591e3dcaf40ad4cc89a053b9a50850d71481edb4d14"), 1660845772),
		new(800000, new uint256("00000000000000000002a7c4c1e48d76c5a37902165a270156b7a8d72728a054"), new uint256("c1da53f9b2d36779e58a21dcf5cde5406fff746fbaf2566211b32ae2ee643def"), 1690168629),
		new(830000, new uint256("000000000000000000011d55599ed27d7efca05f5849b755319c89eb2cffbc1f"), new uint256("cbf1486c05a49194885decc340c65dcb1544a1c5ee7bb4080ed1cde2f1a832e5"), 1707675457),
		new(840000, new uint256("0000000000000000000320283a032748cef8227873ff4872689bf23f1cda83a5"), new uint256("daa02bcedf7574eb64427d8ec8806293b235bedd4585a9b445eca7df2b0087d6"), 1713571767),
		new(850000, new uint256("00000000000000000002a0b5db2a7f8d9087464c2586b546be7bce8eb53b8187"), new uint256("689d30a16371a0e6a4ad59a5cbc3c36c532c94c1ed7c472330568850e2535b57"), 1719689674),
		new(860000, new uint256("0000000000000000000095dd5c0c8e176a6498eb335c491b96df1a1ae178bfbd"), new uint256("c05dd289c01558dd54b96ec1c75e674d5a7b21635f5cac3bd221732fd43f4cc6"), 1725539528),
		new(870000, new uint256("0000000000000000000152dd9d6059126e4e4dbc2732246bef2b8496ef1d971d"), new uint256("62b5b7ca0e0d897d206efb8c83dc37672c6a51c09924ffe56feefe5a5dd75759"), 1731419746),
		new(880000, new uint256("000000000000000000010b17283c3c400507969a9c2afd1dcf2082ec5cca2880"), new uint256("2f3efaeef1a1280e8aa0ab4026861a5fe0414a20447e81e823a21d8d77d905d4"), 1737337343),
		new(890000, new uint256("00000000000000000001f49fb5fb37753c06d78f1a811d707aebf6194bb147e5"), new uint256("0d1f4775c19f9be19c4d81429f0396bc510e841f5ef8653c9cb9900d1d85af29"), 1743269341),
		new(900000, new uint256("000000000000000000010538edbfd2d5b809a33dd83f284aeea41c6d0d96968a"), new uint256("8627fbbd8fb0acbcaac7f7360ee2d9e7ef033350d1b509d4d49b4b6d3a69f447"), 1749188499),
		new(910000, new uint256("0000000000000000000108970acb9522ffd516eae17acddcb1bd16469194a821"), new uint256("da4739c18a678baf86f1318d4d7820425a4964fe7f25a96d42a146dcc98e3870"), 1755163555),
		new(920000, new uint256("000000000000000000005e9de5d9e008d923ced659896d9ad012e347882bdc87"), new uint256("2f33608e443ed7f1a849bc70bca21b68974b16769682ff3cc2ff413d2c6030f8"), 1760995477),
		new(930000, new uint256("00000000000000000000df0f80044130bb616de91fb0a1d61f27cdcf20458233"), new uint256("8c0eb22b34748ba731062fb3bee7fa56d0b0352d3f9fc234a111cd0c8a78d1a6"), 1767006884),
		new(940000, new uint256("000000000000000000002afe1e2f7e176047529419532b2a6773c45623a02c12"), new uint256("13354f38f4ec14b6f7f35c2d29a48a5b4311a27a9bc048502e3fbd12bd982f4f"), 1773063620),
		new(950000, new uint256("000000000000000000010b93c9ea1c29fea277383f0f7d1f26de8b5802e885ff"), new uint256("a8802c59c7313f4c261064448d81247ab167d6168246f4baa24e07f14a38a30c"), 1779141269),
		new(960000, new uint256("000000000000000000001268aab06132c2dd203f77b6020462cd177942d6959d"), new uint256("4f4a81ca55cff6868b20c3bb3c0394ea3580eade5d0a512b6d411ee0ef3e6f0f"), 1785256412),
	];

	public static Checkpoint[] ForNetwork(Network network)
	{
		if (network == Network.Main)
		{
			return Mainnet;
		}
		var genesis = network.GetGenesis();
		var filter = GolombRiceFilterBuilder.BuildBasicFilter(genesis);
		return [new(0, network.GenesisHash, filter.GetHeader(uint256.Zero), genesis.Header.BlockTime.ToUnixTimeSeconds())];
	}

	public static uint NewWalletBirthday(Network network)
	{
		var checkpoints = ForNetwork(network);
		return checkpoints.LastOrDefault(x => x.BlockTime <= DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds(), checkpoints[0]).Height;
	}

	public static FilterModel StartingFilter(Network network)
	{
		var checkpoint = ForNetwork(network)[0];
		var previousHash = checkpoint.Height == 0 ? network.GetGenesis().Header.HashPrevBlock : SmartHeader.GetStartingHeader(network, IndexType.SegwitTaproot).PrevHash;
		// This marker is never scanned; wallet scanning starts at the next height.
		return new(new SmartHeader(checkpoint.BlockHash, previousHash, checkpoint.Height, checkpoint.BlockTime), new GolombRiceFilter([0], 19, 784931));
	}

	internal static void CheckBlockAnchors(ConcurrentChain chain, Network network)
	{
		foreach (var checkpoint in ForNetwork(network).Where(x => x.Height <= chain.Height))
		{
			if (chain.GetBlock((int)checkpoint.Height)?.HashBlock != checkpoint.BlockHash)
			{
				throw new InvalidOperationException($"Block chain does not match checkpoint {checkpoint.Height}.");
			}
		}
	}
}
