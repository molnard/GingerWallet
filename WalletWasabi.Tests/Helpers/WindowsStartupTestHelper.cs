using Microsoft.Win32;
using System.Linq;
using System.Runtime.InteropServices;

namespace WalletWasabi.Tests.Helpers;

public class WindowsStartupTestHelper
{
	private const string PathToRegistryKey = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run";

	public bool RegistryKeyExists()
	{
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			return false;
		}

		using RegistryKey? registryKey = Registry.CurrentUser.OpenSubKey(PathToRegistryKey, false);
		return registryKey?.GetValueNames().Contains(nameof(WalletWasabi)) is true;
	}
}
