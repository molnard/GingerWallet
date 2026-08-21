using System.Security.Cryptography;
using WalletWasabi.Helpers;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Helpers;

public class TwoFactorAuthenticationHelpersTests
{
	private const string Prefix = "2fa-v2:";

	[Fact]
	public void AuthenticatedEncryptionRoundTrips()
	{
		const string plainText = "{\"wallet\":\"authenticated\"}";
		const string secret = "server-generated-secret";

		string encrypted = TwoFactorAuthenticationHelpers.EncryptString(plainText, secret);

		Assert.StartsWith(Prefix, encrypted, StringComparison.Ordinal);
		Assert.True(TwoFactorAuthenticationHelpers.IsUsingAuthenticatedEncryption(encrypted));
		Assert.Equal(plainText, TwoFactorAuthenticationHelpers.DecryptString(encrypted, secret));
	}

	[Fact]
	public void AuthenticatedEncryptionRejectsTampering()
	{
		const string secret = "server-generated-secret";
		string encrypted = TwoFactorAuthenticationHelpers.EncryptString("wallet data", secret);
		byte[] payload = Convert.FromBase64String(encrypted[Prefix.Length..]);
		payload[^1] ^= 1;
		string tampered = Prefix + Convert.ToBase64String(payload);

		Assert.Throws<CryptographicException>(() => TwoFactorAuthenticationHelpers.DecryptString(tampered, secret));
	}

	[Fact]
	public void LegacyEncryptionRemainsReadable()
	{
		const string legacyCipherText = "AAECAwQFBgcICQoLDA0OD9jstQUxVz1YuwT0kiVad37zAZY49iUjI2TWHpG+otBZ";

		string decrypted = TwoFactorAuthenticationHelpers.DecryptString(legacyCipherText, "server-generated-secret");

		Assert.False(TwoFactorAuthenticationHelpers.IsUsingAuthenticatedEncryption(legacyCipherText));
		Assert.Equal("{\"wallet\":\"legacy\"}", decrypted);
	}
}
