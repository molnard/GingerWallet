using ReactiveUI;
using WalletWasabi.Fluent.AddWallet.Models;
using WalletWasabi.Fluent.Navigation.ViewModels;
using WalletWasabi.Lang;

namespace WalletWasabi.Fluent.AddWallet.ViewModels.Create;

[NavigationMetaData(NavigationTarget = NavigationTarget.DialogScreen)]
public partial class WalletBackupTypeViewModel : RoutableViewModel
{
	[AutoNotify] private bool _useMultiShare;

	public WalletBackupTypeViewModel(WalletCreationOptions options)
	{
		Title = Resources.WalletBackupType;
		EnableBack = true;
		SetupCancel(enableCancel: UiContext.WalletRepository.HasWallet, enableCancelOnEscape: UiContext.WalletRepository.HasWallet, enableCancelOnPressed: false);
		NextCommand = ReactiveCommand.Create(() =>
		{
			switch (options)
			{
				case WalletCreationOptions.AddNewWallet add when UseMultiShare:
					UiContext.Navigate().To().MultiShareOptions(add);
					break;
				case WalletCreationOptions.AddNewWallet add:
					UiContext.Navigate().To().RecoveryWords(add);
					break;
				case WalletCreationOptions.RecoverWallet rec when UseMultiShare:
					UiContext.Navigate().To().RecoverMultiShareWallet(rec);
					break;
				case WalletCreationOptions.RecoverWallet rec:
					UiContext.Navigate().To().RecoverWallet(rec);
					break;
			}
		});
	}
}
