using System.Linq;
using System.Globalization;
using System.Reactive.Linq;
using System.Threading.Tasks;
using ReactiveUI;
using WalletWasabi.Fluent.AddWallet.Models;
using WalletWasabi.Fluent.Navigation.ViewModels;
using WalletWasabi.Lang;
using WalletWasabi.Wallets.Slip39;

namespace WalletWasabi.Fluent.AddWallet.ViewModels.Create;

[NavigationMetaData(NavigationTarget = NavigationTarget.DialogScreen)]
public partial class MultiShareBackupViewModel : RoutableViewModel
{
	private readonly WalletCreationOptions.AddNewWallet _options;
	private int _shareIndex;
	[AutoNotify] private string _currentShareWords;
	[AutoNotify] private string _shareProgress;
	[AutoNotify] private string _confirmation = "";
	[AutoNotify] private bool _isConfirming;

	public MultiShareBackupViewModel(WalletCreationOptions.AddNewWallet options)
	{
		ArgumentNullException.ThrowIfNull(options.Shares);
		_options = options;
		Title = Resources.MultiShareBackup;
		_currentShareWords = options.Shares[0].ToMnemonic(WordList.Wordlist);
		_shareProgress = Progress();
		EnableBack = true;
		SetupCancel(enableCancel: UiContext.WalletRepository.HasWallet, enableCancelOnEscape: UiContext.WalletRepository.HasWallet, enableCancelOnPressed: false);
		NextCommand = ReactiveCommand.CreateFromTask(OnNextAsync,
			this.WhenAnyValue(x => x.IsConfirming, x => x.Confirmation).Select(_ => !IsConfirming || MatchesShare()));
	}

	private string Progress() => string.Format(CultureInfo.CurrentCulture, Resources.ShareProgress, _shareIndex + 1, _options.Shares!.Length);

	private bool MatchesShare() => string.Join(" ", Confirmation.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant() == CurrentShareWords;

	private async Task OnNextAsync()
	{
		if (!IsConfirming)
		{
			IsConfirming = true;
			return;
		}
		if (!MatchesShare()) { return; }
		if (_shareIndex < _options.Shares!.Length - 1)
		{
			_shareIndex++;
			CurrentShareWords = _options.Shares[_shareIndex].ToMnemonic(WordList.Wordlist);
			ShareProgress = Progress();
			Confirmation = "";
			IsConfirming = false;
			return;
		}

		var password = await UiContext.Navigate().To().CreatePasswordDialog(Resources.AddPassphrase, Resources.MultiSharePassphrase, enableEmpty: true, allowWhitespace: true).GetResultAsync();
		if (password is null) { return; }
		if (password.Any(c => c is < ' ' or > '~'))
		{
			await ShowErrorAsync(Title, Resources.MultiSharePassphraseAscii, Resources.UnableToRecoverWallet);
			return;
		}
		IsBusy = true;
		try
		{
			var options = _options with { Password = password };
			var wallet = await UiContext.WalletRepository.NewWalletAsync(options);
			UiContext.Navigate().To().AddedWalletPage(wallet, options);
		}
		finally { IsBusy = false; }
	}
}
