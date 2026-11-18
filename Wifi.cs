using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

sealed class WifiProfile
{
    public readonly Guid Interface;
    public readonly string Name;
    public readonly string Adapter;
    public readonly bool Current;
    public bool Duplicate;

    public WifiProfile(Guid id, string name, string adapter, bool current)
    {
        Interface = id;
        Name = name;
        Adapter = adapter;
        Current = current;
    }

    public override string ToString()
    {
        return Name + (Duplicate ? " · " + Adapter : "") + (Current ? "（当前连接）" : "");
    }
}

sealed class Wifi : IDisposable
{
    const int Connected = 1, AdHoc = 2, CurrentConnection = 7, AccessDenied = 5;
    const int NameLength = 256;
    readonly IntPtr handle;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct InterfaceInfo
    {
        public Guid Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = NameLength)] public string Description;
        public int State;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ProfileInfo
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = NameLength)] public string Name;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ConnectionHeader
    {
        public int State;
        public int Mode;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = NameLength)] public string Profile;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ConnectParameters
    {
        public int Mode;
        [MarshalAs(UnmanagedType.LPWStr)] public string Profile;
        public IntPtr Ssid;
        public IntPtr BssidList;
        public int BssType;
        public uint Flags;
    }

    [DllImport("wlanapi.dll")] static extern uint WlanOpenHandle(uint version, IntPtr reserved, out uint actual, out IntPtr handle);
    [DllImport("wlanapi.dll")] static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserved);
    [DllImport("wlanapi.dll")] static extern uint WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr data);
    [DllImport("wlanapi.dll")] static extern uint WlanGetProfileList(IntPtr handle, ref Guid id, IntPtr reserved, out IntPtr data);
    [DllImport("wlanapi.dll")] static extern uint WlanQueryInterface(IntPtr handle, ref Guid id, int opcode, IntPtr reserved, out uint size, out IntPtr data, IntPtr valueType);
    [DllImport("wlanapi.dll")] static extern uint WlanConnect(IntPtr handle, ref Guid id, ref ConnectParameters parameters, IntPtr reserved);
    [DllImport("wlanapi.dll")] static extern void WlanFreeMemory(IntPtr data);

    public Wifi()
    {
        uint version;
        Ensure(WlanOpenHandle(2, IntPtr.Zero, out version, out handle));
    }

    public void Dispose() { WlanCloseHandle(handle, IntPtr.Zero); }

    static void Ensure(uint result)
    {
        if (result != 0) throw new Win32Exception((int)result);
    }

    static T[] ReadList<T>(IntPtr data) where T : struct
    {
        try
        {
            int length = Marshal.ReadInt32(data), itemSize = Marshal.SizeOf(typeof(T));
            var items = new T[length];
            // These WLAN lists begin with a count and an index, both DWORDs.
            for (int i = 0; i < length; i++)
                items[i] = (T)Marshal.PtrToStructure(IntPtr.Add(data, 8 + i * itemSize), typeof(T));
            return items;
        }
        finally { WlanFreeMemory(data); }
    }

    InterfaceInfo[] Interfaces()
    {
        IntPtr data;
        Ensure(WlanEnumInterfaces(handle, IntPtr.Zero, out data));
        return ReadList<InterfaceInfo>(data);
    }

    string CurrentProfile(InterfaceInfo info)
    {
        if (info.State != Connected && info.State != AdHoc) return null;
        uint size;
        IntPtr data;
        var result = WlanQueryInterface(handle, ref info.Id, CurrentConnection, IntPtr.Zero,
            out size, out data, IntPtr.Zero);
        if (result == AccessDenied) Ensure(result);
        if (result != 0) return null; // The interface can disconnect between enumeration and query.
        try
        {
            if (size < Marshal.SizeOf(typeof(ConnectionHeader))) return null;
            return ((ConnectionHeader)Marshal.PtrToStructure(data, typeof(ConnectionHeader))).Profile;
        }
        finally { WlanFreeMemory(data); }
    }

    public WifiProfile[] Profiles()
    {
        var profiles = new List<WifiProfile>();
        foreach (var info in Interfaces())
        {
            var id = info.Id;
            string current;
            try { current = CurrentProfile(info); }
            catch (Win32Exception error)
            {
                if (error.NativeErrorCode != AccessDenied) throw;
                current = null;
            }
            IntPtr data;
            Ensure(WlanGetProfileList(handle, ref id, IntPtr.Zero, out data));
            foreach (var item in ReadList<ProfileInfo>(data))
                profiles.Add(new WifiProfile(id, item.Name, info.Description, item.Name == current));
        }
        foreach (var group in profiles.GroupBy(profile => profile.Name).Where(group => group.Count() > 1))
            foreach (var profile in group) profile.Duplicate = true;
        return profiles.OrderByDescending(profile => profile.Current).ThenBy(profile => profile.Name).ToArray();
    }

    public bool IsConnected(WifiProfile profile)
    {
        return Interfaces().Any(info => info.Id == profile.Interface && CurrentProfile(info) == profile.Name);
    }

    public static IPAddress[] LocalAddresses(WifiProfile profile)
    {
        var nic = SelectedAdapter(profile);
        if (nic == null) return new IPAddress[0];
        return nic.GetIPProperties().UnicastAddresses
            .Select(item => item.Address)
            .Where(address => !address.IsIPv6LinkLocal)
            .ToArray();
    }

    public static string LocalMac(WifiProfile profile)
    {
        var nic = SelectedAdapter(profile);
        var bytes = nic == null ? new byte[0] : nic.GetPhysicalAddress().GetAddressBytes();
        return bytes.Length == 6 ? BitConverter.ToString(bytes).ToLowerInvariant().Replace('-', ':') : null;
    }

    static NetworkInterface SelectedAdapter(WifiProfile profile)
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(nic => { Guid id; return Guid.TryParse(nic.Id, out id) && id == profile.Interface; });
    }

    public void Connect(WifiProfile profile)
    {
        var id = profile.Interface;
        var parameters = new ConnectParameters { Profile = profile.Name, BssType = 1 };
        Ensure(WlanConnect(handle, ref id, ref parameters, IntPtr.Zero));
    }
}
