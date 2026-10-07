using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using ReactiveUI;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Fluent.AddWallet.Models;
using WalletWasabi.Fluent.Models.Wallets;
using WalletWasabi.Fluent.Navigation.ViewModels;
using WalletWasabi.Lang;
using WalletWasabi.Wallets.Slip39;

namespace WalletWasabi.Fluent.AddWallet.ViewModels;

[NavigationMetaData(NavigationTarget = NavigationTarget.DialogScreen)]
public partial class RecoverMultiShareWalletViewModel : RoutableViewModel
{
	[AutoNotify] private string _sharesText = "";
	[AutoNotify] private string _validationMessage = "";
	private int _minGapLimit = 114;

	public RecoverMultiShareWalletViewModel(WalletCreationOptions.RecoverWallet options, WalletModel? walletToVerify = null)
	{
		Title = Resources.MultiShareBackup;
		EnableBack = true;
		SetupCancel(enableCancel: UiContext.WalletRepository.HasWallet, enableCancelOnEscape: UiContext.WalletRepository.HasWallet, enableCancelOnPressed: false);
		var canExecute = this.WhenAnyValue(x => x.SharesText).Select(text =>
		{
			var valid = TryParseShares(out _);
			ValidationMessage = valid || string.IsNullOrWhiteSpace(SharesText) ? "" : Resources.InvalidMultiShareSet;
			return valid;
		});
		NextCommand = ReactiveCommand.CreateFromTask(async () =>
		{
			if (!TryParseShares(out var shares)) { return; }
			var password = await UiContext.Navigate().To().CreatePasswordDialog(Resources.EnterPassphrase, Resources.MultiSharePassphrase, enableEmpty: true, allowWhitespace: true).GetResultAsync();
			if (password is null) { return; }
			IsBusy = true;
			try
			{
				if (walletToVerify is { } wallet)
				{
					var recovered = await Task.Run(() => KeyManager.RecoverMultiShare(shares, password, wallet.Wallet.Network,
						wallet.Wallet.KeyManager.SegwitAccountKeyPath, wallet.Wallet.KeyManager.TaprootAccountKeyPath));
					if (recovered.SegwitExtPubKey != wallet.Wallet.KeyManager.SegwitExtPubKey || recovered.TaprootExtPubKey != wallet.Wallet.KeyManager.TaprootExtPubKey)
					{
						await ShowErrorAsync(Title, Resources.InvalidMultiShareSet, Resources.UnableToRecoverWallet);
						return;
					}
					UiContext.Navigate().To().Success();
				}
				else
				{
					var recovery = options with { Shares = shares, Password = password, MinGapLimit = _minGapLimit };
					var walletSettings = await UiContext.WalletRepository.NewWalletAsync(recovery);
					UiContext.Navigate().To().AddedWalletPage(walletSettings, recovery);
				}
			}
			catch (ArgumentException)
			{
				await ShowErrorAsync(Title, Resources.InvalidMultiShareSet, Resources.UnableToRecoverWallet);
			}
			catch (NotSupportedException)
			{
				await ShowErrorAsync(Title, Resources.MultiSharePassphraseAscii, Resources.UnableToRecoverWallet);
			}
			finally { IsBusy = false; }
		}, canExecute);
		AdvancedRecoveryOptionsDialogCommand = ReactiveCommand.CreateFromTask(async () =>
		{
			var result = await UiContext.Navigate().To().AdvancedRecoveryOptions(_minGapLimit).GetResultAsync();
			if (result is { } value) { _minGapLimit = value; }
		});
	}

	public ICommand AdvancedRecoveryOptionsDialogCommand { get; }

	private bool TryParseShares(out Share[] shares)
	{
		shares = [];
		try
		{
			var lines = SharesText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
			if (lines.Length is < 1 or > 256) { return false; }
			shares = lines.Select(Share.FromMnemonic).ToArray();
			Shamir.SelectRecoveryShares(shares);
			return true;
		}
		catch (ArgumentException) { return false; }
	}
}
