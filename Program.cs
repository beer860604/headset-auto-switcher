// HyperX Cloud Stinger 2 Wireless auto output switcher.
//
// The USB receiver stays enumerated when the headset powers off, so the Windows
// device list cannot show that change. The receiver's vendor HID collection COL02
// does report it: 64-01 = headset on, 64-03 = headset off. Unplugging the receiver
// ends the HID read, which is treated as disconnected.
//
// Other headsets are taught through LearnForm, which saves a HeadsetProfile: either a
// pair of HID reports like the above, or the output's own Windows connection state.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        bool created;
        using (var mutex = new Mutex(true, "BtAudioSwitcher.HyperX", out created))
        {
            if (!created) return;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(args.Contains("--background")));
        }
    }
}

static class Log
{
    static readonly object Gate = new object();
    public static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BluetoothAudioSwitcher", "log.txt");

    public static void Write(string message)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 1024 * 1024) info.Delete();
                File.AppendAllText(FilePath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine,
                    Encoding.UTF8);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

static class Settings
{
    const string KeyPath = @"Software\BluetoothAudioSwitcher";
    const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "BluetoothAudioSwitcher";

    public const string AutoTarget = "自動（切到連上的耳機）";
    public const string HyperXOutput = "喇叭 (HyperX Cloud Stinger 2 Wireless)";

    public static string Get(string name, string fallback)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(KeyPath))
        {
            return key.GetValue(name, fallback) as string ?? fallback;
        }
    }

    public static void Set(string name, string value)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(KeyPath))
        {
            key.SetValue(name, value);
        }
    }

    public static List<HeadsetProfile> LearnedProfiles()
    {
        return Get("LearnedHeadsets", "")
            .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(HeadsetProfile.Parse)
            .Where(p => p != null)
            .ToList();
    }

    public static void SaveLearnedProfiles(IEnumerable<HeadsetProfile> profiles)
    {
        Set("LearnedHeadsets", string.Join("\n", profiles.Select(p => p.Serialize())));
    }

    // A learned profile for the HyperX output replaces the built-in one.
    public static List<HeadsetProfile> AllProfiles()
    {
        var profiles = LearnedProfiles();
        if (!profiles.Any(p => p.Output == HyperXOutput)) profiles.Add(HeadsetProfile.HyperX());
        return profiles;
    }

    public static bool Autostart
    {
        get
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunPath))
            {
                return key != null && key.GetValue(RunName) != null;
            }
        }
        set
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunPath, true))
            {
                if (value) key.SetValue(RunName, "\"" + Application.ExecutablePath + "\" --background");
                else key.DeleteValue(RunName, false);
            }
        }
    }
}

// Interface language. "auto" follows Windows: Chinese systems get Chinese, others English.
static class L
{
    public const string Auto = "auto", Chinese = "zh", English = "en";
    static bool english;

    public static string Choice
    {
        get { return Settings.Get("Language", Auto); }
    }

    public static void Load()
    {
        string choice = Choice;
        english = choice == English
            || (choice != Chinese && !System.Globalization.CultureInfo.CurrentUICulture.Name.StartsWith("zh"));
    }

    public static void Choose(string choice)
    {
        Settings.Set("Language", choice);
        Load();
    }

    public static string T(string chinese, string englishText)
    {
        return english ? englishText : chinese;
    }
}

class HeadsetProfile
{
    public string Output;
    public string DeviceTag;     // HID path tag; empty when Windows shows the connection itself
    public string CollectionTag;
    public string OnReport;      // hex prefix, such as 64-01
    public string OffReport;

    public bool UsesEndpoint
    {
        get { return string.IsNullOrEmpty(DeviceTag); }
    }

    public static HeadsetProfile HyperX()
    {
        return new HeadsetProfile
        {
            Output = Settings.HyperXOutput,
            DeviceTag = "vid_03f0&pid_0d93",
            CollectionTag = "col02",
            OnReport = "64-01",
            OffReport = "64-03"
        };
    }

    public string Serialize()
    {
        return string.Join("\t", new[] { Output, DeviceTag ?? "", CollectionTag ?? "", OnReport ?? "", OffReport ?? "" });
    }

    public static HeadsetProfile Parse(string line)
    {
        string[] parts = line.Split('\t');
        if (parts.Length != 5) return null;
        return new HeadsetProfile
        {
            Output = parts[0],
            DeviceTag = parts[1],
            CollectionTag = parts[2],
            OnReport = parts[3],
            OffReport = parts[4]
        };
    }
}

// ---------------------------------------------------------------- audio endpoints

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
class MMDeviceEnumeratorCom { }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator
{
    void EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
    void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
}

[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceCollection
{
    void GetCount(out int count);
    void Item(int index, out IMMDevice device);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice
{
    void Activate(ref Guid iid, int context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object result);
    void OpenPropertyStore(int access, out IPropertyStore store);
    void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    void GetState(out int state);
}

[ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IPropertyStore
{
    void GetCount(out int count);
    void GetAt(int index, out PropertyKey key);
    void GetValue(ref PropertyKey key, out PropVariant value);
}

[StructLayout(LayoutKind.Sequential)]
struct PropertyKey
{
    public Guid Format;
    public int Id;
}

[StructLayout(LayoutKind.Sequential)]
struct PropVariant
{
    public ushort Type, Reserved1, Reserved2, Reserved3;
    public IntPtr Pointer;
    public IntPtr Extra;
}

[ComImport, Guid("2A07407E-6497-4A18-9787-32F79BD0D98F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IDeviceTopology
{
    void GetConnectorCount(out int count);
    void GetConnector(int index, out IConnector connector);
}

[ComImport, Guid("9C2C4058-23F5-41DE-877A-DF3AF236A09E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IConnector
{
    void Slot1(); void Slot2(); void Slot3(); void Slot4(); void Slot5(); void Slot6(); void Slot7();
    void GetDeviceIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);
}

[ComImport, Guid("28F54685-06FD-11D2-B27A-00A0C9223196"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IKsControl
{
    [PreserveSig]
    int KsProperty(ref KsPropertyRequest property, int propertyLength, IntPtr data, int dataLength, out int returned);
}

[StructLayout(LayoutKind.Sequential)]
struct KsPropertyRequest
{
    public Guid Set;
    public int Id;
    public int Flags;
}

[ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
class PolicyConfigCom { }

// Undocumented interface; the ten placeholders only keep the vtable slots in order.
[ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IPolicyConfig
{
    void Slot1(); void Slot2(); void Slot3(); void Slot4(); void Slot5();
    void Slot6(); void Slot7(); void Slot8(); void Slot9(); void Slot10();
    void SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
}

class AudioDevice
{
    public string Id;
    public string Name;
    public string Enumerator;
    public string UsbTag;   // vid_xxxx&pid_xxxx of a USB output, otherwise null
    public bool Connected;

    // Hands-free endpoints are the same headset's low-quality call profile.
    public bool IsHandsFree
    {
        get { return "BTHHFENUM".Equals(Enumerator, StringComparison.OrdinalIgnoreCase); }
    }

    public bool IsBluetoothHeadset
    {
        get
        {
            return Enumerator != null
                && Enumerator.StartsWith("BTH", StringComparison.OrdinalIgnoreCase)
                && !IsHandsFree;
        }
    }

    public override string ToString()
    {
        return Connected ? Name : Name + L.T("（未連線）", " (disconnected)");
    }
}

static class Audio
{
    const int Render = 0;
    const int StateActive = 1;
    const int StateNotPresent = 4;
    const int StateUnplugged = 8;
    const int ClsctxAll = 23;
    const int KsPropertyGet = 1;
    const int KsReconnect = 0; // KSPROPERTY_ONESHOT_RECONNECT
    const int PropertyFriendlyName = 14;
    const int PropertyEnumeratorName = 24;
    const int PropertyHardwarePath = 2;
    const ushort VtLpwstr = 31;
    static readonly Guid DeviceProperties = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0");
    static readonly Guid EndpointProperties = new Guid("B3F8FA53-0004-438E-9003-51A46E139BFC");

    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant value);

    static IMMDeviceEnumerator Enumerator()
    {
        return (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
    }

    static AudioDevice Describe(IMMDevice device)
    {
        string id;
        device.GetId(out id);
        IPropertyStore store;
        device.OpenPropertyStore(0, out store);
        int state;
        device.GetState(out state);
        return new AudioDevice
        {
            Id = id,
            Name = ReadString(store, DeviceProperties, PropertyFriendlyName),
            Enumerator = ReadString(store, DeviceProperties, PropertyEnumeratorName),
            UsbTag = UsbTagOf(ReadString(store, EndpointProperties, PropertyHardwarePath)),
            Connected = state == StateActive
        };
    }

    static string ReadString(IPropertyStore store, Guid format, int propertyId)
    {
        var key = new PropertyKey { Format = format, Id = propertyId };
        PropVariant value;
        store.GetValue(ref key, out value);
        string text = value.Type == VtLpwstr ? Marshal.PtrToStringUni(value.Pointer) : null;
        PropVariantClear(ref value);
        return text;
    }

    // The path looks like {1}.USB\VID_03F0&PID_0D93&MI_00\8&F3DD75F&0&0000
    static string UsbTagOf(string hardwarePath)
    {
        if (hardwarePath == null) return null;
        Match match = Regex.Match(hardwarePath, @"\.USB\\(VID_[0-9A-F]{4}&PID_[0-9A-F]{4})", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
    }

    // True for a known output whose device is away, such as an unplugged USB receiver.
    public static bool ExistsOnSystem(string name)
    {
        return Outputs(StateActive | StateNotPresent | StateUnplugged).Any(d => d.Name == name);
    }

    public static List<AudioDevice> ActiveOutputs()
    {
        return Outputs(StateActive);
    }

    // Includes unplugged endpoints, such as a paired Bluetooth headset that is away.
    public static List<AudioDevice> AllOutputs()
    {
        return Outputs(StateActive | StateUnplugged).Where(d => !d.IsHandsFree).ToList();
    }

    // Asks the Bluetooth driver to connect. Success only means an attempt was made.
    public static int ReconnectBluetooth(string id)
    {
        var enumerator = Enumerator();
        IMMDevice endpoint;
        enumerator.GetDevice(id, out endpoint);

        object activated;
        Guid iid = typeof(IDeviceTopology).GUID;
        endpoint.Activate(ref iid, ClsctxAll, IntPtr.Zero, out activated);
        IConnector connector;
        ((IDeviceTopology)activated).GetConnector(0, out connector);
        string adapterId;
        connector.GetDeviceIdConnectedTo(out adapterId);

        IMMDevice adapter;
        enumerator.GetDevice(adapterId, out adapter);
        iid = typeof(IKsControl).GUID;
        adapter.Activate(ref iid, ClsctxAll, IntPtr.Zero, out activated);

        var request = new KsPropertyRequest
        {
            Set = new Guid("7FA06C40-B8F6-4C7E-8556-E8C33A12E54D"), // KSPROPSETID_BtAudio
            Id = KsReconnect,
            Flags = KsPropertyGet
        };
        int returned;
        return ((IKsControl)activated).KsProperty(ref request, Marshal.SizeOf(request), IntPtr.Zero, 0, out returned);
    }

    static List<AudioDevice> Outputs(int stateMask)
    {
        var result = new List<AudioDevice>();
        IMMDeviceCollection devices;
        Enumerator().EnumAudioEndpoints(Render, stateMask, out devices);
        int count;
        devices.GetCount(out count);
        for (int i = 0; i < count; i++)
        {
            try
            {
                IMMDevice device;
                devices.Item(i, out device);
                result.Add(Describe(device));
            }
            catch (COMException) { } // endpoint vanished while listing
        }
        return result;
    }

    public static AudioDevice CurrentOutput()
    {
        try
        {
            IMMDevice device;
            Enumerator().GetDefaultAudioEndpoint(Render, 1, out device);
            return Describe(device);
        }
        catch (COMException)
        {
            return null; // no active output device
        }
    }

    public static void SetOutput(string id)
    {
        var policy = (IPolicyConfig)new PolicyConfigCom();
        for (int role = 0; role < 3; role++) policy.SetDefaultEndpoint(id, role);
    }
}

// ---------------------------------------------------------------- headset state

enum HeadsetState { Unknown, Connected, Disconnected }

class HeadsetWatcher
{
    const int RetryMs = 2000;

    readonly HeadsetProfile profile;
    readonly Action<HeadsetState, string> onChange;
    volatile bool stopped;
    volatile SafeFileHandle openHandle;

    public HeadsetWatcher(HeadsetProfile profile, Action<HeadsetState, string> onChange)
    {
        this.profile = profile;
        this.onChange = onChange;
    }

    public void Start()
    {
        var thread = new Thread(Loop);
        thread.IsBackground = true;
        thread.Start();
    }

    public void Stop()
    {
        stopped = true;
        Hid.CancelRead(openHandle);
    }

    void Report(HeadsetState state, string reason)
    {
        if (!stopped) onChange(state, reason);
    }

    void Loop()
    {
        bool receiverPresent = true; // so a missing receiver is reported once at startup
        while (!stopped)
        {
            string path = Hid.FindPath(profile.DeviceTag, profile.CollectionTag);
            SafeFileHandle handle = path == null ? null : Hid.OpenForRead(path);
            if (handle == null)
            {
                if (receiverPresent) Report(HeadsetState.Disconnected, "找不到接收器");
                receiverPresent = false;
                Thread.Sleep(RetryMs);
                continue;
            }

            receiverPresent = true;
            openHandle = handle;
            Log.Write("接收器已連接：" + profile.Output);
            int length = Hid.InputLength(handle);
            QueryInitialState(handle, length);
            ReadReports(handle, length);
            Report(HeadsetState.Disconnected, "接收器已拔除");
            if (!stopped) Thread.Sleep(RetryMs);
        }
    }

    // Most receivers only push changes, so ask once for the state at connect time.
    void QueryInitialState(SafeFileHandle handle, int length)
    {
        if (length == 0) return;
        var report = new byte[length];
        report[0] = Convert.ToByte(profile.OnReport.Substring(0, 2), 16);
        if (Hid.GetInputReport(handle, report))
        {
            Log.Write("接收器目前狀態回應：" + BitConverter.ToString(report));
            Handle(report, report.Length, "啟動時查詢");
        }
        else
        {
            Log.Write("接收器不支援狀態查詢，等待下一次開關機訊號（錯誤 " + Marshal.GetLastWin32Error() + "）");
        }
    }

    void ReadReports(SafeFileHandle handle, int length)
    {
        if (length == 0) return;
        try
        {
            using (var stream = new FileStream(handle, FileAccess.Read, 1, true))
            {
                var buffer = new byte[length];
                while (true)
                {
                    int read = stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0) return;
                    Handle(buffer, read, "HID 訊號");
                }
            }
        }
        catch (IOException) { }                 // receiver unplugged
        catch (OperationCanceledException) { }  // receiver unplugged or watcher stopped
    }

    void Handle(byte[] report, int length, string reason)
    {
        string hex = BitConverter.ToString(report, 0, length);
        bool on = hex.StartsWith(profile.OnReport, StringComparison.Ordinal);
        bool off = hex.StartsWith(profile.OffReport, StringComparison.Ordinal);
        if (!on && !off) return;
        Log.Write("HID 訊號：" + hex);
        Report(on ? HeadsetState.Connected : HeadsetState.Disconnected, reason);
    }
}

// Listens to a USB device's HID interfaces during learning. It only reads; it sends nothing.
class HidCapture
{
    public const int Baseline = 0, HeadsetOff = 1, HeadsetOn = 2;
    const int Stopped = -1;
    const int UsageGeneric = 0x01, UsageConsumer = 0x0C, UsageDigitizer = 0x0D; // mouse, volume keys, touch
    const int MaxDistinctReports = 8; // more than this is a counter or sensor, not a status

    class Entry
    {
        public string Path;
        public string Hex;
        public int Phase;
    }

    class Candidate
    {
        public string Path;
        public string Off;
        public string On;
        public int Distinct;
    }

    readonly object gate = new object();
    readonly List<Entry> entries = new List<Entry>();
    readonly List<SafeFileHandle> handles = new List<SafeFileHandle>();
    volatile int phase = Baseline;

    public int Phase
    {
        set { phase = value; }
    }

    public int Start(string usbTag)
    {
        foreach (string path in Hid.FindPaths(usbTag))
        {
            SafeFileHandle handle = Hid.OpenForRead(path);
            if (handle == null) continue; // Windows keeps some interfaces to itself

            int length, usagePage;
            Hid.GetCaps(handle, out length, out usagePage);
            if (length == 0 || usagePage == UsageGeneric || usagePage == UsageConsumer || usagePage == UsageDigitizer)
            {
                handle.Dispose();
                continue;
            }

            handles.Add(handle);
            string capturedPath = path;
            var thread = new Thread(delegate() { Read(capturedPath, handle, length); });
            thread.IsBackground = true;
            thread.Start();
        }
        return handles.Count;
    }

    public void Stop()
    {
        phase = Stopped;
        foreach (SafeFileHandle handle in handles) Hid.CancelRead(handle);
    }

    void Read(string path, SafeFileHandle handle, int length)
    {
        try
        {
            using (var stream = new FileStream(handle, FileAccess.Read, 1, true))
            {
                var buffer = new byte[length];
                while (true)
                {
                    int read = stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0) return;
                    int current = phase;
                    if (current == Stopped) return;
                    lock (gate)
                    {
                        entries.Add(new Entry { Path = path, Hex = BitConverter.ToString(buffer, 0, read), Phase = current });
                    }
                }
            }
        }
        catch (IOException) { }
        catch (OperationCanceledException) { }
    }

    // A status report shows up in one step only, and never while nothing changed.
    public HeadsetProfile BuildProfile(string output)
    {
        List<Entry> seen;
        lock (gate) seen = entries.ToList();

        var candidates = new List<Candidate>();
        foreach (var group in seen.GroupBy(e => e.Path))
        {
            var baseline = group.Where(e => e.Phase == Baseline).Select(e => e.Hex).ToList();
            var off = group.Where(e => e.Phase == HeadsetOff).Select(e => e.Hex).ToList();
            var on = group.Where(e => e.Phase == HeadsetOn).Select(e => e.Hex).ToList();
            var offOnly = off.Where(h => !baseline.Contains(h) && !on.Contains(h)).ToList();
            var onOnly = on.Where(h => !baseline.Contains(h) && !off.Contains(h)).ToList();
            if (offOnly.Count == 0 || onOnly.Count == 0) continue;

            int distinct = offOnly.Distinct().Count() + onOnly.Distinct().Count();
            if (distinct > MaxDistinctReports) continue;
            candidates.Add(new Candidate { Path = group.Key, Off = offOnly.Last(), On = onOnly.Last(), Distinct = distinct });
        }

        Candidate best = candidates.OrderBy(c => c.Distinct).ThenBy(c => c.On.Length).FirstOrDefault();
        if (best == null) return null;

        int keep = BytesToCompare(best.On, best.Off);
        return new HeadsetProfile
        {
            Output = output,
            DeviceTag = Hid.InterfaceTag(best.Path),
            CollectionTag = "",
            OnReport = FirstBytes(best.On, keep),
            OffReport = FirstBytes(best.Off, keep)
        };
    }

    // Bytes after the first difference can hold things like battery level, so they are ignored.
    static int BytesToCompare(string a, string b)
    {
        string[] x = a.Split('-'), y = b.Split('-');
        int count = Math.Min(x.Length, y.Length);
        for (int i = 0; i < count; i++)
        {
            if (x[i] != y[i]) return i + 1;
        }
        return count;
    }

    static string FirstBytes(string hex, int count)
    {
        return string.Join("-", hex.Split('-').Take(count));
    }
}

static class Hid
{
    [StructLayout(LayoutKind.Sequential)]
    struct InterfaceData
    {
        public int Size;
        public Guid ClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct HidCaps
    {
        public ushort Usage, UsagePage, InputLength, OutputLength, FeatureLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 27)] public ushort[] Reserved;
    }

    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll", SetLastError = true)]
    static extern bool HidD_GetInputReport(SafeFileHandle handle, byte[] report, int length);
    [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);
    [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr data, out HidCaps caps);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CancelIoEx(SafeFileHandle handle, IntPtr overlapped);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr SetupDiGetClassDevs(ref Guid guid, IntPtr enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid guid, uint index, ref InterfaceData data);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref InterfaceData data, IntPtr detail, uint size, out uint required, IntPtr info);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    public static string FindPath(string deviceTag, string collectionTag)
    {
        return FindPaths(deviceTag).FirstOrDefault(p => p.ToLowerInvariant().Contains(collectionTag));
    }

    public static List<string> FindPaths(string deviceTag)
    {
        const uint PresentInterfaces = 0x12;
        var paths = new List<string>();
        Guid guid;
        HidD_GetHidGuid(out guid);
        IntPtr set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, PresentInterfaces);
        try
        {
            for (uint i = 0; ; i++)
            {
                var data = new InterfaceData { Size = Marshal.SizeOf(typeof(InterfaceData)) };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data)) return paths;
                string path = DetailPath(set, ref data);
                if (path != null && path.ToLowerInvariant().Contains(deviceTag)) paths.Add(path);
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    // \\?\hid#vid_03f0&pid_0d93&mi_03&col02#7&...  ->  vid_03f0&pid_0d93&mi_03&col02
    public static string InterfaceTag(string path)
    {
        return path.Split('#')[1].ToLowerInvariant();
    }

    static string DetailPath(IntPtr set, ref InterfaceData data)
    {
        uint required;
        SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out required, IntPtr.Zero);
        IntPtr buffer = Marshal.AllocHGlobal((int)required);
        try
        {
            Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
            if (!SetupDiGetDeviceInterfaceDetail(set, ref data, buffer, required, out required, IntPtr.Zero)) return null;
            return Marshal.PtrToStringUni(new IntPtr(buffer.ToInt64() + 4));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static SafeFileHandle OpenForRead(string path)
    {
        const uint GenericRead = 0x80000000, ShareReadWrite = 3, OpenExisting = 3, Overlapped = 0x40000000;
        SafeFileHandle handle = CreateFile(path, GenericRead, ShareReadWrite, IntPtr.Zero, OpenExisting, Overlapped, IntPtr.Zero);
        return handle.IsInvalid ? null : handle;
    }

    public static bool GetInputReport(SafeFileHandle handle, byte[] report)
    {
        return HidD_GetInputReport(handle, report, report.Length);
    }

    public static void GetCaps(SafeFileHandle handle, out int inputLength, out int usagePage)
    {
        inputLength = 0;
        usagePage = 0;
        IntPtr data;
        if (!HidD_GetPreparsedData(handle, out data)) return;
        HidCaps caps;
        HidP_GetCaps(data, out caps);
        HidD_FreePreparsedData(data);
        inputLength = caps.InputLength;
        usagePage = caps.UsagePage;
    }

    public static int InputLength(SafeFileHandle handle)
    {
        int length, usagePage;
        GetCaps(handle, out length, out usagePage);
        return length;
    }

    public static void CancelRead(SafeFileHandle handle)
    {
        if (handle == null || handle.IsClosed) return;
        try { CancelIoEx(handle, IntPtr.Zero); }
        catch (ObjectDisposedException) { } // closed by the reader at the same moment
    }
}

// ---------------------------------------------------------------- learning

// Steps the user through one off/on cycle, then saves how that headset can be told apart.
class LearnForm : Form
{
    const int BaselineSeconds = 5;
    const int OffSeconds = 5;
    const int OnSeconds = 8;

    enum Step { Choose, Baseline, Off, On, Done }

    readonly ComboBox outputBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly Label stepLabel = new Label();
    readonly Label learnedLabel = new Label { ForeColor = Color.DimGray };
    readonly Button nextButton = new Button();
    readonly Button cancelButton = new Button { Text = L.T("取消", "Cancel") };
    readonly Button clearButton = new Button { Text = L.T("清除學習結果", "Clear learned") };
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 1000 };

    Step step = Step.Choose;
    HidCapture capture;
    AudioDevice target;
    string nextText;
    int secondsLeft;
    bool awayWhenOff;

    public bool Changed { get; private set; }
    public HeadsetProfile Learned { get; private set; }

    public LearnForm(Icon icon)
    {
        Text = L.T("學習新耳機（實驗功能）", "Learn a headset (experimental)");
        Icon = icon;
        Font = new Font("Microsoft JhengHei UI", 9F);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(420, 240);

        AddAt(new Label { Text = L.T("要學習哪副耳機？藍芽耳機不用學，已經會自動切換。",
            "Which headset? Bluetooth headsets already switch automatically.") }, 16, 14, 388, 20);
        AddAt(outputBox, 16, 38, 388, 24);
        AddAt(stepLabel, 16, 74, 388, 76);
        AddAt(learnedLabel, 16, 156, 388, 36);
        AddAt(clearButton, 16, 200, 120, 28);
        AddAt(cancelButton, 214, 200, 90, 28);
        AddAt(nextButton, 314, 200, 90, 28);

        foreach (AudioDevice output in Audio.ActiveOutputs()
            .Where(d => !d.IsBluetoothHeadset)
            .OrderBy(d => d.UsbTag == null))
        {
            outputBox.Items.Add(output);
        }
        if (outputBox.Items.Count > 0) outputBox.SelectedIndex = 0;

        stepLabel.Font = new Font(Font.FontFamily, 10F);
        stepLabel.Text = L.T("請先打開耳機，確認它有聲音，再按「開始」。\n全程只會讀取耳機接收器的訊號，不會送任何指令給它。",
            "Turn the headset on, make sure you hear sound, then press Start.\nThis only reads the receiver's signals and never sends it commands.");
        nextButton.Text = L.T("開始", "Start");
        nextButton.Enabled = outputBox.Items.Count > 0;
        ShowLearned();

        nextButton.Click += delegate { Next(); };
        cancelButton.Click += delegate { Close(); };
        clearButton.Click += delegate { ClearLearned(); };
        timer.Tick += delegate { Tick(); };
    }

    void AddAt(Control control, int x, int y, int width, int height)
    {
        control.Location = new Point(x, y);
        control.Size = new Size(width, height);
        Controls.Add(control);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        timer.Stop();
        if (capture != null) capture.Stop();
        base.OnFormClosed(e);
    }

    void ShowLearned()
    {
        var learned = Settings.LearnedProfiles();
        clearButton.Enabled = learned.Count > 0;
        learnedLabel.Text = learned.Count == 0
            ? L.T("尚未學習任何耳機。", "No headsets learned yet.")
            : L.T("已學習：", "Learned: ") + string.Join(L.T("、", ", "), learned.Select(p => p.Output));
    }

    void ClearLearned()
    {
        Settings.SaveLearnedProfiles(new HeadsetProfile[0]);
        Log.Write("已清除學習結果");
        Changed = true;
        Learned = null;
        ShowLearned();
    }

    void Next()
    {
        switch (step)
        {
            case Step.Choose:
                target = (AudioDevice)outputBox.SelectedItem;
                outputBox.Enabled = false;
                clearButton.Enabled = false;
                capture = new HidCapture();
                int interfaces = target.UsbTag == null ? 0 : capture.Start(target.UsbTag);
                Log.Write("開始學習：" + target.Name + "，可讀取的 HID 介面 " + interfaces + " 個");
                step = Step.Baseline;
                stepLabel.Text = L.T("請先不要動耳機，正在確認平常的訊號…", "Leave the headset alone while normal signals are checked…");
                WaitThenAllow(L.T("下一步", "Next"), BaselineSeconds);
                break;
            case Step.Off:
                awayWhenOff = !IsTargetActive();
                capture.Phase = HidCapture.HeadsetOn;
                step = Step.On;
                stepLabel.Text = L.T("第 2 步：打開耳機（有線耳機請接回去）。\n等耳機連上後，再按「完成」。",
                    "Step 2: Turn the headset on (plug a wired one back in).\nOnce it has connected, press Finish.");
                WaitThenAllow(L.T("完成", "Finish"), OnSeconds);
                break;
            case Step.On:
                Finish();
                break;
            case Step.Done:
                Close();
                break;
        }
    }

    void Tick()
    {
        if (--secondsLeft > 0)
        {
            nextButton.Text = nextText + " (" + secondsLeft + ")";
            return;
        }
        timer.Stop();
        if (step == Step.Baseline)
        {
            capture.Phase = HidCapture.HeadsetOff;
            step = Step.Off;
            stepLabel.Text = L.T("第 1 步：關閉耳機（有線耳機請拔掉）。\n關好之後，再按「下一步」。",
                "Step 1: Turn the headset off (unplug a wired one).\nOnce it is off, press Next.");
            WaitThenAllow(L.T("下一步", "Next"), OffSeconds);
            return;
        }
        nextButton.Text = nextText;
        nextButton.Enabled = true;
    }

    // The wait leaves time for a signal to arrive before the next step starts.
    void WaitThenAllow(string text, int seconds)
    {
        nextText = text;
        secondsLeft = seconds;
        nextButton.Enabled = false;
        nextButton.Text = text + " (" + seconds + ")";
        timer.Start();
    }

    bool IsTargetActive()
    {
        return Audio.ActiveOutputs().Any(d => d.Name == target.Name);
    }

    void Finish()
    {
        capture.Stop();
        HeadsetProfile profile = awayWhenOff && IsTargetActive()
            ? new HeadsetProfile { Output = target.Name }
            : capture.BuildProfile(target.Name);

        step = Step.Done;
        nextButton.Text = L.T("關閉", "Close");
        cancelButton.Enabled = false;

        if (profile == null)
        {
            Log.Write("學習失敗：" + target.Name);
            stepLabel.Text = L.T("這副耳機學不到：關開時沒有收到可辨認的訊號。\n手動切換還是可以用。",
                "Couldn't learn this headset: no recognizable signal when it turned off and on.\nManual switching still works.");
            ShowLearned();
            return;
        }

        var learned = Settings.LearnedProfiles().Where(p => p.Output != profile.Output).ToList();
        learned.Add(profile);
        Settings.SaveLearnedProfiles(learned);
        Changed = true;
        Learned = profile;
        Log.Write("學習完成：" + profile.Serialize().Replace('\t', '|'));
        stepLabel.Text = profile.UsesEndpoint
            ? L.T("學會了！Windows 看得到這副耳機的連線變化，之後會自動切換。",
                "Learned! Windows can see this headset connect, so it will switch automatically.")
            : L.T("學會了！收到耳機的開關訊號（開 " + profile.OnReport + "，關 " + profile.OffReport + "），之後開關耳機會自動切換。",
                "Learned! Got its signals (on " + profile.OnReport + ", off " + profile.OffReport + "), so turning it on or off will switch automatically.");
        ShowLearned();
    }
}

// ---------------------------------------------------------------- window and tray

class MainForm : Form
{
    const int MaxSwitchAttempts = 15;
    const int BluetoothScanMs = 2000;

    readonly NotifyIcon tray = new NotifyIcon();
    readonly Label headsetLabel = new Label();
    readonly ComboBox outputBox = NewCombo();
    readonly ToolStripMenuItem switchMenu = new ToolStripMenuItem();
    readonly ComboBox languageBox = NewCombo();
    readonly List<Action> relabel = new List<Action>(); // reapplies each fixed text after a language change
    readonly ComboBox connectedBox = NewCombo();
    readonly ComboBox disconnectedBox = NewCombo();
    readonly CheckBox autoCheck = new CheckBox();
    readonly CheckBox startupCheck = new CheckBox();
    readonly ToolStripMenuItem autoMenu = new ToolStripMenuItem();
    readonly ToolStripMenuItem startupMenu = new ToolStripMenuItem();
    readonly System.Windows.Forms.Timer retryTimer = new System.Windows.Forms.Timer();
    readonly System.Windows.Forms.Timer bluetoothTimer = new System.Windows.Forms.Timer();

    // Output names of connected headsets, most recently connected last.
    readonly List<string> connectedHeadsets = new List<string>();
    readonly HashSet<string> endpointHeadsets = new HashSet<string>();

    // Headsets whose state comes from a receiver's HID reports, by output name.
    readonly Dictionary<string, HeadsetState> receiverStates = new Dictionary<string, HeadsetState>();
    readonly List<HeadsetWatcher> watchers = new List<HeadsetWatcher>();
    List<HeadsetProfile> profiles = Settings.AllProfiles();

    bool startHidden;
    bool exiting;
    bool loading;
    bool learning;
    bool autoEnabled;
    string pendingTarget;
    int attemptsLeft;

    public MainForm(bool background)
    {
        startHidden = background;
        L.Load();
        autoEnabled = Settings.Get("AutoSwitch", "1") == "1";
        BuildWindow();
        BuildTray();

        retryTimer.Interval = 1000;
        retryTimer.Tick += delegate { TrySwitch(); };

        var ignored = Handle; // BeginInvoke below needs the handle even while hidden
        RefreshDeviceLists();
        Log.Write("程式啟動（含學習模式版）");
        LoadProfiles(null);
        RefreshOutputs();

        bluetoothTimer.Interval = BluetoothScanMs;
        bluetoothTimer.Tick += delegate
        {
            ScanBluetooth();
            if (Visible && !outputBox.DroppedDown && !retryTimer.Enabled) RefreshOutputs();
        };
        bluetoothTimer.Start();
    }

    // justLearned was switched on a moment ago, so it needs no further signal to count as connected.
    void LoadProfiles(HeadsetProfile justLearned)
    {
        foreach (HeadsetWatcher watcher in watchers) watcher.Stop();
        watchers.Clear();

        profiles = Settings.AllProfiles();
        var receiverProfiles = profiles.Where(p => !p.UsesEndpoint).ToList();
        foreach (string name in receiverStates.Keys.ToList())
        {
            if (receiverProfiles.Any(p => p.Output == name)) continue;
            receiverStates.Remove(name);
            connectedHeadsets.Remove(name);
        }

        foreach (HeadsetProfile profile in receiverProfiles)
        {
            string output = profile.Output;
            if (!receiverStates.ContainsKey(output)) receiverStates[output] = HeadsetState.Unknown;
            var watcher = new HeadsetWatcher(profile, delegate(HeadsetState next, string reason)
            {
                BeginInvoke(new Action(delegate { OnHeadsetChanged(output, next, reason); }));
            });
            watchers.Add(watcher);
            watcher.Start();
        }

        ScanBluetooth();
        ShowHeadsetState();
        if (justLearned != null && !justLearned.UsesEndpoint)
        {
            OnHeadsetChanged(justLearned.Output, HeadsetState.Connected, "學習完成");
        }
    }

    void LearnHeadset()
    {
        if (learning) return;
        learning = true;
        using (var form = new LearnForm(Icon))
        {
            form.ShowDialog();
            if (form.Changed) LoadProfiles(form.Learned);
        }
        learning = false;
    }

    static ComboBox NewCombo()
    {
        return new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 280 };
    }

    protected override void SetVisibleCore(bool value)
    {
        if (startHidden)
        {
            startHidden = false; // only the first show from Application.Run is suppressed
            value = false;
        }
        base.SetVisibleCore(value);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        tray.Visible = false;
        base.OnFormClosing(e);
    }

    void BuildWindow()
    {
        Translate(() => Text = AppTitle);
        Font = new Font("Microsoft JhengHei UI", 9F);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(420, 225);
        using (Stream icon = typeof(MainForm).Assembly.GetManifestResourceStream("app.ico")) Icon = new Icon(icon);

        AddAt(headsetLabel, 16, 14, 390);
        AddAt(Translated(new Label(), "切換輸出", "Output"), 16, 39, 110);
        AddAt(outputBox, 126, 36, 280);
        AddAt(Translated(new Label(), "耳機連線時輸出到", "On connect"), 16, 72, 110);
        AddAt(connectedBox, 126, 69, 280);
        AddAt(Translated(new Label(), "耳機斷線時輸出到", "On disconnect"), 16, 104, 110);
        AddAt(disconnectedBox, 126, 101, 280);

        AddAt(Translated(autoCheck, "自動切換", "Auto switch"), 16, 136, 100);
        AddAt(Translated(startupCheck, "開機自動啟動", "Run at startup"), 126, 136, 130);
        var learnButton = new Button();
        AddAt(Translated(learnButton, "學習新耳機…", "Learn headset…"), 296, 133, 110);
        learnButton.Click += delegate { LearnHeadset(); };
        AddAt(Translated(new Label { ForeColor = Color.DimGray },
            "關閉視窗後，程式會繼續在右下角運作。", "Keeps running in the tray after closing."), 16, 170, 270);
        languageBox.Items.AddRange(new object[] { "Auto", "繁體中文", "English" });
        languageBox.SelectedIndex = L.Choice == L.Chinese ? 1 : L.Choice == L.English ? 2 : 0;
        AddAt(languageBox, 296, 167, 110);
        languageBox.SelectionChangeCommitted += delegate
        {
            SetLanguage(languageBox.SelectedIndex == 1 ? L.Chinese : languageBox.SelectedIndex == 2 ? L.English : L.Auto);
        };

        connectedBox.SelectedIndexChanged += delegate { SaveChoice("ConnectedDevice", connectedBox); };
        disconnectedBox.SelectedIndexChanged += delegate { SaveChoice("DisconnectedDevice", disconnectedBox); };
        connectedBox.DropDown += delegate { RefreshDeviceLists(); };
        disconnectedBox.DropDown += delegate { RefreshDeviceLists(); };
        autoCheck.CheckedChanged += delegate { if (!loading) SetAuto(autoCheck.Checked); };
        startupCheck.CheckedChanged += delegate { if (!loading) SetStartup(startupCheck.Checked); };
        outputBox.DropDown += delegate { RefreshOutputs(); };
        outputBox.SelectionChangeCommitted += delegate
        {
            var chosen = outputBox.SelectedItem as AudioDevice;
            if (chosen != null) SwitchManually(chosen);
        };
        Shown += delegate { RefreshDeviceLists(); RefreshOutputs(); };
        ShowOptions();
    }

    void AddAt(Control control, int x, int y, int width)
    {
        control.Location = new Point(x, y);
        control.Width = width;
        Controls.Add(control);
    }

    static string AppTitle
    {
        get { return L.T("耳機自動切換", "Headset Auto Switcher"); }
    }

    void Translate(Action apply)
    {
        apply();
        relabel.Add(apply);
    }

    Control Translated(Control control, string chinese, string english)
    {
        Translate(() => control.Text = L.T(chinese, english));
        return control;
    }

    void TranslateItem(ToolStripItem item, string chinese, string english)
    {
        Translate(() => item.Text = L.T(chinese, english));
    }

    void SetLanguage(string choice)
    {
        L.Choose(choice);
        foreach (Action apply in relabel) apply();
        ShowOptions();
        ShowHeadsetState();
        RefreshDeviceLists();
        RefreshOutputs();
    }

    void BuildTray()
    {
        var menu = new ContextMenuStrip();
        menu.Opening += delegate { RefreshSwitchMenu(); };
        TranslateItem(switchMenu, "切換輸出", "Switch output");
        menu.Items.Add(switchMenu);
        menu.Items.Add(new ToolStripSeparator());
        TranslateItem(menu.Items.Add("", null, delegate { ShowWindow(); }), "開啟視窗", "Open window");
        TranslateItem(menu.Items.Add("", null, delegate { LearnHeadset(); }), "學習新耳機…", "Learn headset…");
        TranslateItem(menu.Items.Add("", null, delegate { OpenLog(); }), "開啟記錄檔", "Open log");
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(autoMenu);
        menu.Items.Add(startupMenu);
        menu.Items.Add(new ToolStripSeparator());
        TranslateItem(menu.Items.Add("", null, delegate { exiting = true; Close(); }), "結束", "Exit");
        autoMenu.Click += delegate { SetAuto(!autoEnabled); };
        startupMenu.Click += delegate { SetStartup(!Settings.Autostart); };

        tray.Icon = new Icon(Icon, SystemInformation.SmallIconSize);
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += delegate { ShowWindow(); };
        tray.Visible = true;
        ShowOptions();
    }

    void ShowWindow()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    void OpenLog()
    {
        if (File.Exists(Log.FilePath)) Process.Start(Log.FilePath);
    }

    void SetAuto(bool enabled)
    {
        autoEnabled = enabled;
        Settings.Set("AutoSwitch", enabled ? "1" : "0");
        Log.Write("自動切換：" + (enabled ? "開啟" : "暫停"));
        if (!enabled) retryTimer.Stop();
        ShowOptions();
    }

    void SetStartup(bool enabled)
    {
        Settings.Autostart = enabled;
        Log.Write("開機自動啟動：" + (enabled ? "開啟" : "關閉"));
        ShowOptions();
    }

    void ShowOptions()
    {
        loading = true;
        bool startup = Settings.Autostart;
        autoCheck.Checked = autoEnabled;
        startupCheck.Checked = startup;
        autoMenu.Text = L.T("自動切換：", "Auto switch: ") + (autoEnabled ? L.T("開啟", "on") : L.T("暫停", "paused"));
        startupMenu.Text = L.T("開機自動啟動：", "Run at startup: ") + (startup ? L.T("開啟", "on") : L.T("關閉", "off"));
        loading = false;
    }

    // The saved value stays Settings.AutoTarget whatever language shows.
    static string AutoLabel
    {
        get { return L.T(Settings.AutoTarget, "Auto (latest connected headset)"); }
    }

    void SaveChoice(string name, ComboBox box)
    {
        if (loading || box.SelectedItem == null) return;
        string chosen = (string)box.SelectedItem;
        Settings.Set(name, chosen == AutoLabel ? Settings.AutoTarget : chosen);
    }

    void RefreshDeviceLists()
    {
        loading = true;
        var names = Audio.ActiveOutputs().Select(d => d.Name).ToList();
        var connectedChoices = new List<string> { AutoLabel };
        connectedChoices.AddRange(names);
        string connected = Settings.Get("ConnectedDevice", Settings.AutoTarget);
        FillCombo(connectedBox, connectedChoices, connected == Settings.AutoTarget ? AutoLabel : connected);
        FillCombo(disconnectedBox, names, DisconnectedTarget());
        loading = false;
    }

    static void FillCombo(ComboBox box, List<string> names, string selected)
    {
        box.Items.Clear();
        foreach (string name in names) box.Items.Add(name);
        if (selected == null) return;
        if (!names.Contains(selected)) box.Items.Add(selected); // keep a device that is currently unplugged
        box.SelectedItem = selected;
    }

    // On first use this picks the current output, or else the first one that isn't a headset.
    string DisconnectedTarget()
    {
        string saved = Settings.Get("DisconnectedDevice", null);
        if (saved != null) return saved;

        var outputs = Audio.ActiveOutputs()
            .Where(d => !d.IsBluetoothHeadset && !profiles.Any(p => p.Output == d.Name))
            .ToList();
        AudioDevice current = Audio.CurrentOutput();
        AudioDevice chosen = outputs.FirstOrDefault(d => current != null && d.Id == current.Id)
            ?? outputs.FirstOrDefault();
        if (chosen == null) return null;

        Settings.Set("DisconnectedDevice", chosen.Name);
        return chosen.Name;
    }

    // A receiver headset stays listed when the receiver is out, and shows as away when it is off.
    List<AudioDevice> SwitchableOutputs()
    {
        var outputs = Audio.AllOutputs();
        foreach (var receiver in receiverStates)
        {
            AudioDevice output = outputs.FirstOrDefault(d => d.Name == receiver.Key);
            if (output != null)
            {
                if (receiver.Value == HeadsetState.Disconnected) output.Connected = false;
            }
            else if (Audio.ExistsOnSystem(receiver.Key))
            {
                outputs.Add(new AudioDevice { Name = receiver.Key });
            }
        }
        return outputs;
    }

    void RefreshOutputs()
    {
        AudioDevice current = Audio.CurrentOutput();
        var outputs = SwitchableOutputs();
        outputBox.Items.Clear();
        foreach (AudioDevice output in outputs) outputBox.Items.Add(output);
        if (current != null) outputBox.SelectedItem = outputs.FirstOrDefault(d => d.Name == current.Name);
    }

    void RefreshSwitchMenu()
    {
        AudioDevice current = Audio.CurrentOutput();
        switchMenu.DropDownItems.Clear();
        foreach (AudioDevice output in SwitchableOutputs())
        {
            AudioDevice target = output;
            var item = new ToolStripMenuItem(target.ToString());
            item.Checked = current != null && current.Name == target.Name;
            item.Click += delegate { SwitchManually(target); };
            switchMenu.DropDownItems.Add(item);
        }
    }

    void SwitchManually(AudioDevice target)
    {
        Log.Write("手動切換：" + target);
        if (!target.Connected && target.IsBluetoothHeadset) RequestBluetoothConnect(target);
        pendingTarget = target.Name;
        attemptsLeft = MaxSwitchAttempts;
        TrySwitch();
    }

    void RequestBluetoothConnect(AudioDevice target)
    {
        try
        {
            int result = Audio.ReconnectBluetooth(target.Id);
            Log.Write("要求藍芽重新連線，回應 0x" + result.ToString("X8"));
        }
        catch (COMException e)
        {
            Log.Write("無法要求藍芽重新連線（0x" + e.ErrorCode.ToString("X8") + "）");
        }
        catch (InvalidCastException e)
        {
            Log.Write("無法要求藍芽重新連線：" + e.Message);
        }
    }

    void ShowHeadsetState()
    {
        string prefix = L.T("藍芽耳機：", "Headset: ");
        string text = connectedHeadsets.Count > 0 ? L.T("已連線", "connected")
            : receiverStates.ContainsValue(HeadsetState.Unknown)
                ? L.T("偵測中（下次開關耳機後更新）", "detecting (updates next power cycle)")
            : L.T("未連線", "not connected");
        headsetLabel.Text = prefix + text
            + (connectedHeadsets.Count > 0 ? L.T("（", " (") + connectedHeadsets.Last() + L.T("）", ")") : "");
        tray.Text = prefix + text; // NotifyIcon rejects text over 63 characters, so no device name
    }

    void OnHeadsetChanged(string output, HeadsetState next, string reason)
    {
        HeadsetState previous;
        if (!receiverStates.TryGetValue(output, out previous) || next == previous) return;
        receiverStates[output] = next;
        Log.Write("耳機狀態：" + (next == HeadsetState.Connected ? "已連線" : "未連線") + "（" + reason + "）：" + output);
        connectedHeadsets.Remove(output);
        if (next == HeadsetState.Connected) connectedHeadsets.Add(output);
        ApplyHeadsets();
    }

    // Headsets Windows can see (Bluetooth, and learned ones) leave the active list when disconnected.
    void ScanBluetooth()
    {
        var learned = new HashSet<string>(profiles.Where(p => p.UsesEndpoint).Select(p => p.Output));
        var current = new HashSet<string>(Audio.ActiveOutputs()
            .Where(d => d.IsBluetoothHeadset || learned.Contains(d.Name))
            .Select(d => d.Name));
        var added = current.Except(endpointHeadsets).ToList();
        var removed = endpointHeadsets.Except(current).ToList();
        if (added.Count == 0 && removed.Count == 0) return;

        foreach (string name in removed)
        {
            Log.Write("耳機已斷線：" + name);
            endpointHeadsets.Remove(name);
            connectedHeadsets.Remove(name);
        }
        foreach (string name in added)
        {
            Log.Write("耳機已連線：" + name);
            endpointHeadsets.Add(name);
            connectedHeadsets.Add(name);
        }
        ApplyHeadsets();
    }

    void ApplyHeadsets()
    {
        ShowHeadsetState();
        if (!autoEnabled) return;

        pendingTarget = connectedHeadsets.Count == 0 ? DisconnectedTarget() : ConnectedTarget();
        if (pendingTarget == null) return;
        attemptsLeft = MaxSwitchAttempts;
        TrySwitch();
    }

    string ConnectedTarget()
    {
        string chosen = Settings.Get("ConnectedDevice", Settings.AutoTarget);
        return chosen == Settings.AutoTarget ? connectedHeadsets.Last() : chosen;
    }

    // After a replug the endpoint can appear a few seconds later than the HID report.
    void TrySwitch()
    {
        AudioDevice target = Audio.ActiveOutputs().FirstOrDefault(d => d.Name == pendingTarget);
        if (target != null)
        {
            retryTimer.Stop();
            ApplyOutput(target);
            return;
        }
        if (--attemptsLeft > 0)
        {
            retryTimer.Start();
            return;
        }
        retryTimer.Stop();
        Log.Write("找不到輸出裝置，未切換：" + pendingTarget);
        tray.ShowBalloonTip(3000, AppTitle, L.T("無法切換到：", "Couldn't switch to: ") + pendingTarget, ToolTipIcon.Warning);
        RefreshOutputs();
    }

    void ApplyOutput(AudioDevice target)
    {
        AudioDevice current = Audio.CurrentOutput();
        if (current != null && current.Id == target.Id)
        {
            Log.Write("輸出已經是：" + target.Name);
            return;
        }
        try
        {
            Audio.SetOutput(target.Id);
            Log.Write("已切換輸出：" + target.Name);
        }
        catch (COMException e)
        {
            Log.Write("切換失敗：" + target.Name + "（0x" + e.ErrorCode.ToString("X8") + "）");
        }
        RefreshOutputs();
    }
}
