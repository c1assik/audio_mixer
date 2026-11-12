using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Mixer.Plugins.Catalog;

/// <summary>Подпись файла модуля: WinVerifyTrust (без сети и проверки отзыва) и имя подписавшего из сертификата.</summary>
internal static unsafe class Authenticode
{
    private const uint TRUST_E_NOSIGNATURE = 0x800B0100;
    private const uint TRUST_E_SUBJECT_FORM_UNKNOWN = 0x800B0003;
    private const uint TRUST_E_PROVIDER_UNKNOWN = 0x800B0001;

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public char* pcwszFilePath;
        public IntPtr hFile;
        public Guid* pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public WINTRUST_FILE_INFO* pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public char* pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll")]
    private static extern uint WinVerifyTrust(IntPtr hwnd, Guid* action, WINTRUST_DATA* data);

    public static FileSignature Check(string file)
    {
        uint result;
        fixed (char* path = file)
        {
            var info = new WINTRUST_FILE_INFO { cbStruct = (uint)sizeof(WINTRUST_FILE_INFO), pcwszFilePath = path };
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)sizeof(WINTRUST_DATA),
                dwUIChoice = 2,          // WTD_UI_NONE
                dwUnionChoice = 1,       // WTD_CHOICE_FILE
                pFile = &info,
                dwStateAction = 1,       // WTD_STATEACTION_VERIFY
                dwProvFlags = 0x1000 | 0x10, // WTD_CACHE_ONLY_URL_RETRIEVAL | WTD_REVOCATION_CHECK_NONE: скан не ходит в сеть
            };
            Guid action = GenericVerifyV2;
            result = WinVerifyTrust(new IntPtr(-1), &action, &data);
            data.dwStateAction = 2; // WTD_STATEACTION_CLOSE
            WinVerifyTrust(new IntPtr(-1), &action, &data);
        }
        if (result is TRUST_E_NOSIGNATURE or TRUST_E_SUBJECT_FORM_UNKNOWN or TRUST_E_PROVIDER_UNKNOWN)
            return new FileSignature(SignatureStatus.NotSigned, null);
        return new FileSignature(result == 0 ? SignatureStatus.Valid : SignatureStatus.Invalid, Signer(file));
    }

    private static string? Signer(string file)
    {
        try
        {
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(file));
            return cert.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        }
        catch (CryptographicException)
        {
            return null; // подпись в каталоге Windows, а не в самом файле
        }
    }
}
