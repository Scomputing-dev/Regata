using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Regata.Desktop;

internal static class GroqCredentialStore
{
    private const string CredentialTarget = "Regata/GroqApiKey";
    private const uint GenericCredential = 1;
    private const uint PersistForUser = 2;
    private const int NotFoundError = 1168;

    public static void Save(string apiKey)
    {
        if (!apiKey.StartsWith("gsk_", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("This field expects a Groq API key.");
        }

        var secretBytes = Encoding.Unicode.GetBytes(apiKey);
        var nativeBuffer = Marshal.AllocHGlobal(secretBytes.Length);
        try
        {
            Marshal.Copy(secretBytes, 0, nativeBuffer, secretBytes.Length);
            var credential = new NativeCredential
            {
                Type = GenericCredential,
                TargetName = CredentialTarget,
                UserName = "Regata",
                CredentialBlobSize = checked((uint)secretBytes.Length),
                CredentialBlob = nativeBuffer,
                Persist = PersistForUser
            };

            if (!CredWrite(ref credential, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not save the key in Credential Manager.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secretBytes);
            for (var index = 0; index < secretBytes.Length; index++)
            {
                Marshal.WriteByte(nativeBuffer, index, 0);
            }

            Marshal.FreeHGlobal(nativeBuffer);
        }
    }

    public static string? Read()
    {
        if (!CredRead(CredentialTarget, GenericCredential, 0, out var credentialPointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == NotFoundError)
            {
                return null;
            }

            throw new Win32Exception(error, "Windows could not read the key from Credential Manager.");
        }

        byte[]? secretBytes = null;
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (credential.CredentialBlobSize == 0 || credential.CredentialBlobSize % 2 != 0)
            {
                throw new InvalidOperationException("The saved Groq key could not be read.");
            }

            secretBytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, secretBytes, 0, secretBytes.Length);
            return Encoding.Unicode.GetString(secretBytes);
        }
        finally
        {
            if (secretBytes is not null)
            {
                CryptographicOperations.ZeroMemory(secretBytes);
            }

            CredFree(credentialPointer);
        }
    }

    public static void Delete()
    {
        if (!CredDelete(CredentialTarget, GenericCredential, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != NotFoundError)
            {
                throw new Win32Exception(error, "Windows could not remove the key from Credential Manager.");
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string TargetName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredFree")]
    private static extern void CredFree(IntPtr credential);
}
