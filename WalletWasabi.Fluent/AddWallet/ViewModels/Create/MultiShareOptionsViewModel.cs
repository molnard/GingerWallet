using System.Globalization;
using System.Reactive.Linq;
using System.Threading.Tasks;
using NBitcoin;
using ReactiveUI;
using WalletWasabi.Fluent.AddWallet.Models;
using WalletWasabi.Fluent.Navigation.ViewModels;
using WalletWasabi.Fluent.Validation;
using WalletWasabi.Lang;
using WalletWasabi.Models;
using WalletWasabi.Wallets.Slip39;
using System.Security.Cryptography;

namespace WalletWasabi.Fluent.AddWallet.ViewModels.Create;

[NavigationMetaData(NavigationTarget = NavigationTarget.DialogScreen)]
public partial class MultiShareOptionsViewModel : RoutableViewModel
{
	[AutoNotify] private string _shareCount = "3";
	[AutoNotify] private string _threshold = "2";

	public MultiShareOptionsViewModel(WalletCreationOptions.AddNewWallet options)
	{
		Title = Resources.MultiShareBackup;
		EnableBack = true;
		SetupCancel(enableCancel: UiContext.WalletRepository.HasWallet, enableCancelOnEscape: UiContext.WalletRepository.HasWallet, enableCancelOnPressed: false);
		this.ValidateProperty(x => x.ShareCount, ValidateOptions);
		this.ValidateProperty(x => x.Threshold, ValidateOptions);
		var canExecute = this.WhenAnyValue(x => x.ShareCount, x => x.Threshold).Select(values => TryGetOptions(out _, out _));
		NextCommand = ReactiveCommand.CreateFromTask(async () =>
		{
			if (!TryGetOptions(out var count, out var threshold))
			{
				return;
			}
			IsBusy = true;
			try
			{
				var shares = await Task.Run(() =>
				{
					var entropy = RandomUtils.GetBytes(16);
					try { return Shamir.Generate(threshold, count, entropy); }
					finally { CryptographicOperations.ZeroMemory(entropy); }
				});
				UiContext.Navigate().To().MultiShareBackup(options with { Mnemonic = null, Shares = shares });
			}
			finally { IsBusy = false; }
		}, canExecute);
	}

	private bool TryGetOptions(out byte count, out byte threshold)
	{
		count = threshold = 0;
		return byte.TryParse(ShareCount, NumberStyles.None, CultureInfo.InvariantCulture, out count) &&
			byte.TryParse(Threshold, NumberStyles.None, CultureInfo.InvariantCulture, out threshold) &&
			count is >= 1 and <= 16 && threshold >= 1 && threshold <= count && (threshold != 1 || count == 1);
	}

	private void ValidateOptions(IValidationErrors errors)
	{
		if (!TryGetOptions(out _, out _))
		{
			errors.Add(ErrorSeverity.Error, Resources.InvalidMultiShareOptions);
		}
	}
}
