using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AtlasKamera.Servisler;

/// <summary>
/// Windows "job object" sarmalayıcısı — başlattığımız yardımcı süreçleri
/// uygulamaya bağlar.
///
/// NEDEN: dh-p2p tünelleri normal kapanışta durduruluyor, ama uygulama
/// çökerse ya da görev yöneticisinden sonlandırılırsa geride kalıyordu.
/// Kalan tünel NVR'ın tek P2P oturum yuvasını tutar ve kamera bir daha
/// açılmaz. Kafese alınan süreçler, uygulama nasıl kapanırsa kapansın
/// işletim sistemi tarafından birlikte sonlandırılır.
/// </summary>
public sealed class SurecKafesi : IDisposable
{
    private IntPtr _kafes = IntPtr.Zero;

    public SurecKafesi()
    {
        try
        {
            _kafes = CreateJobObject(IntPtr.Zero, null);
            if (_kafes == IntPtr.Zero) return;

            var bilgi = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
            };
            var genis = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION { BasicLimitInformation = bilgi };

            var boyut = Marshal.SizeOf(genis);
            var isaretci = Marshal.AllocHGlobal(boyut);
            try
            {
                Marshal.StructureToPtr(genis, isaretci, false);
                if (!SetInformationJobObject(_kafes, JobObjectExtendedLimitInformation,
                                             isaretci, (uint)boyut))
                {
                    Gunluk.Yaz("kafes kuralı yazılamadı, Win32 hata " + Marshal.GetLastWin32Error());
                    CloseHandle(_kafes);
                    _kafes = IntPtr.Zero;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(isaretci);
            }
        }
        catch
        {
            _kafes = IntPtr.Zero;      // kafes kurulamazsa uygulama yine çalışır
        }
    }

    public void Ekle(Process surec)
    {
        if (_kafes == IntPtr.Zero)
        {
            Gunluk.Yaz($"kafes kurulamadı: {surec.ProcessName} ({surec.Id}) başıboş kalabilir");
            return;
        }
        try
        {
            if (!AssignProcessToJobObject(_kafes, surec.Handle))
                Gunluk.Yaz($"kafese eklenemedi ({surec.ProcessName} {surec.Id}), " +
                           $"Win32 hata {Marshal.GetLastWin32Error()}");
        }
        catch (Exception e)
        {
            Gunluk.Hata("kafese ekleme", e);
        }
    }

    public void Dispose()
    {
        if (_kafes == IntPtr.Zero) return;
        CloseHandle(_kafes);            // kafes kapanınca içindeki süreçler de kapanır
        _kafes = IntPtr.Zero;
    }

    // ── Win32 ────────────────────────────────────────────────────────────
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr guvenlik, string? ad);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr kafes, int sinif,
        IntPtr bilgi, uint uzunluk);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr kafes, IntPtr surec);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr nesne);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
