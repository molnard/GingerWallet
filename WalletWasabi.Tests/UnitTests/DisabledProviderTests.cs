using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GingerCommon.Providers.ExchangeRateProviders;
using Moq;
using NBitcoin;
using WalletWasabi.Bases;
using WalletWasabi.Daemon;
using WalletWasabi.Daemon.FeeRateProviders;
using WalletWasabi.Fluent.Helpers;
using WalletWasabi.Services;
using WalletWasabi.WebClients.Wasabi;
using Xunit;

namespace WalletWasabi.Tests.UnitTests;

public class DisabledProviderTests
{
	[Fact]
	public async Task DisabledExchangeRatesNeverQueryProviderEvenWhenApplicationActivatesAsync()
	{
		await using var http = new WasabiHttpClientFactory(torEndPoint: null, backendUriGetter: () => new Uri("http://localhost/"));
		var provider = new Mock<ExchangeRateProvider>(MockBehavior.Strict);
		using var service = new ExchangeRateService(TimeSpan.FromSeconds(5), http, "USD", enabled: false, provider.Object);
		service.Active = true;

		await service.RefreshAsync(TimeSpan.Zero, CancellationToken.None);
		await service.StartAsync(CancellationToken.None);
		await service.StopAsync(CancellationToken.None);

		Assert.Null(service.ExchangeRate);
		Assert.False(service.Enabled);
		provider.VerifyNoOtherCalls();
	}

	[Theory]
	[InlineData("Main")]
	[InlineData("TestNet")]
	[InlineData("RegTest")]
	public async Task DisabledFeesSupportServiceLifecycleWithoutSyntheticEstimatesAsync(string networkName)
	{
		var network = Network.GetNetwork(networkName)!;
		await using var http = new WasabiHttpClientFactory(torEndPoint: null, backendUriGetter: () => new Uri("http://localhost/"));
		using var provider = new FeeRateProvider(http, FeeRateProviderSource.None, network);
		provider.Initialize(null);
		await provider.StartAsync(CancellationToken.None);
		await provider.StopAsync(CancellationToken.None);

		Assert.Equal(FeeRateProvider.FeeEstimationDisabledMessage, Assert.Throws<InvalidOperationException>(provider.GetAllFeeEstimate).Message);
		Assert.False(TransactionFeeHelper.TryGetFeeEstimates(provider, network, out var estimates));
		Assert.Null(estimates);
		Assert.Throws<InvalidOperationException>(() => TransactionFeeHelper.GetFeeEstimates(provider, network));
	}

	[Fact]
	public void ConfigurationPreservesExistingDefaultsAndCanDisableProviders()
	{
		Assert.True(JsonSerializer.Deserialize<PersistentConfig>("{}")!.ExchangeRatesEnabled);
		var disabled = new PersistentConfig { ExchangeRatesEnabled = false, FeeRateEstimationProvider = FeeRateProviderSource.None };
		var restored = JsonSerializer.Deserialize<PersistentConfig>(JsonSerializer.Serialize(disabled, ConfigManagerNg.DefaultOptions))!;
		Assert.True(disabled.DeepEquals(restored));
		Assert.False(new PersistentConfig().DeepEquals(disabled));

		var commandLine = new Config(new PersistentConfig(), ["--exchangeratesenabled=false", "--feerateestimationprovider=None"]);
		Assert.False(commandLine.ExchangeRatesEnabled);
		Assert.Equal(FeeRateProviderSource.None, commandLine.FeeRateEstimationProvider);
	}
}
