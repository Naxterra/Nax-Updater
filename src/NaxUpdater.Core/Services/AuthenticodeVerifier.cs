using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace NaxUpdater.Core.Services;

public interface IAuthenticodeVerifier
{
    AuthenticodeVerificationResult Verify(string filePath, string expectedSigner);
}

public sealed class NativeAuthenticodeVerifier : IAuthenticodeVerifier
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public AuthenticodeVerificationResult Verify(string filePath, string expectedSigner)
    {
        if (!File.Exists(filePath))
        {
            return new AuthenticodeVerificationResult(false, null, "The downloaded file does not exist.");
        }

        var fileInformation = new WinTrustFileInfo(filePath);
        var filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInformation, filePointer, false);
            var trustData = new WinTrustData(filePointer);
            var result = WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, ref trustData);
            if (result != 0)
            {
                return new AuthenticodeVerificationResult(false, null, $"Windows rejected the Authenticode signature (0x{result:X8}).");
            }

            try
            {
                using var certificate = LoadAuthenticodeSigner(filePath);
                var signer = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
                var matches = signer.Contains(expectedSigner, StringComparison.OrdinalIgnoreCase) ||
                              certificate.Subject.Contains(expectedSigner, StringComparison.OrdinalIgnoreCase);
                return matches
                    ? new AuthenticodeVerificationResult(true, signer, null)
                    : new AuthenticodeVerificationResult(false, signer, $"Expected signer '{expectedSigner}', found '{signer}'.");
            }
            catch (Exception exception)
            {
                return new AuthenticodeVerificationResult(false, null, $"The signer certificate could not be read: {exception.Message}");
            }
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(filePointer);
            Marshal.FreeHGlobal(filePointer);
        }
    }

    private static X509Certificate2 LoadAuthenticodeSigner(string filePath)
    {
        if (X509Certificate2.GetCertContentType(filePath) != X509ContentType.Authenticode)
        {
            throw new System.Security.Cryptography.CryptographicException("The file does not contain an Authenticode signature.");
        }
#pragma warning disable SYSLIB0057 // .NET runtime guidance for extracting an Authenticode signer on Windows.
        return new X509Certificate2(filePath);
#pragma warning restore SYSLIB0057
    }

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true, SetLastError = true)]
    private static extern int WinVerifyTrust(
        IntPtr windowHandle,
        [MarshalAs(UnmanagedType.LPStruct)] Guid actionId,
        ref WinTrustData trustData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint StructSize;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;

        public WinTrustFileInfo(string filePath)
        {
            StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>();
            FilePath = filePath;
            FileHandle = IntPtr.Zero;
            KnownSubject = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInformation;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;

        public WinTrustData(IntPtr fileInformation)
        {
            StructSize = (uint)Marshal.SizeOf<WinTrustData>();
            PolicyCallbackData = IntPtr.Zero;
            SipClientData = IntPtr.Zero;
            UiChoice = 2; // WTD_UI_NONE
            RevocationChecks = 0;
            UnionChoice = 1; // WTD_CHOICE_FILE
            FileInformation = fileInformation;
            StateAction = 0;
            StateData = IntPtr.Zero;
            UrlReference = IntPtr.Zero;
            ProviderFlags = 0x00000080; // WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT
            UiContext = 0;
            SignatureSettings = IntPtr.Zero;
        }
    }
}

public sealed record AuthenticodeVerificationResult(bool IsValid, string? Signer, string? Error);
