using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace Poppy.Core.Engines;

/// <summary>
/// Checks that a downloaded installer carries a valid Authenticode signature from the expected
/// publisher before Poppy runs it.
/// </summary>
public static class Authenticode
{
    public static bool IsSignedBy(string path, string publisher)
    {
        if (!File.Exists(path) || !Trusted(path)) return false;
        try
        {
#pragma warning disable SYSLIB0057 // reading the signer certificate of a signed file
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            return cert.Subject.Contains(publisher, StringComparison.OrdinalIgnoreCase);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }

    // ---------- WinVerifyTrust ----------

    static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData, pSIPClientData;
        public uint dwUIChoice, fdwRevocationChecks, dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData, pwszURLReference;
        public uint dwProvFlags, dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, ref WINTRUST_DATA data);

    const uint WTD_UI_NONE = 2, WTD_REVOKE_NONE = 0, WTD_CHOICE_FILE = 1, WTD_STATEACTION_VERIFY = 1, WTD_STATEACTION_CLOSE = 2;

    static bool Trusted(string path)
    {
        var file = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = path };
        var filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(file, filePtr, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice = WTD_CHOICE_FILE,
                pFile = filePtr,
                dwStateAction = WTD_STATEACTION_VERIFY,
            };
            var result = WinVerifyTrust(new IntPtr(-1), GenericVerifyV2, ref data);
            data.dwStateAction = WTD_STATEACTION_CLOSE;
            WinVerifyTrust(new IntPtr(-1), GenericVerifyV2, ref data);
            return result == 0;
        }
        finally
        {
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(filePtr);
            Marshal.FreeHGlobal(filePtr);
        }
    }
}
