using System.IO;
using System.Security.Cryptography;
using System.Text;
using WalletWasabi.Crypto;

namespace WalletWasabi.Helpers;

public class WalletEncryption
{
	public required string ClientServerId { get; set; }
}

public static class TwoFactorAuthenticationHelpers
{
	private const string AuthenticatedEncryptionPrefix = "2fa-v2:";

	public static string EncryptString(string plainText, string secret)
	{
		return AuthenticatedEncryptionPrefix + StringCipher.Encrypt(plainText, secret);
	}

	public static string DecryptString(string cipherText, string secret)
	{
		return IsUsingAuthenticatedEncryption(cipherText)
			? StringCipher.Decrypt(cipherText[AuthenticatedEncryptionPrefix.Length..], secret)
			: DecryptLegacyString(cipherText, secret);
	}

	public static bool IsUsingAuthenticatedEncryption(string cipherText) =>
		cipherText.StartsWith(AuthenticatedEncryptionPrefix, StringComparison.Ordinal);

	private static string DecryptLegacyString(string cipherText, string secret)
	{
		using Aes aes = Aes.Create();
		aes.Key = GetEncryptionKey(secret);

		var bytes = Convert.FromBase64String(cipherText);
		using MemoryStream memoryStream = new(bytes);

		byte[] iv = new byte[16];
		memoryStream.Read(iv, 0, iv.Length);
		aes.IV = iv;

		using ICryptoTransform decryptor = aes.CreateDecryptor();
		using CryptoStream cryptoStream = new(memoryStream, decryptor, CryptoStreamMode.Read);
		using StreamReader streamReader = new(cryptoStream);
		return streamReader.ReadToEnd();
	}

	private static byte[] GetEncryptionKey(string secret)
	{
		using SHA256 sha256 = SHA256.Create();
		return sha256.ComputeHash(Encoding.UTF8.GetBytes(secret));
	}
}
