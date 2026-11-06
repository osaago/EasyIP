using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace EasyIP
{
    /// <summary>
    /// Reads adapters and changes their IPv4 settings.
    /// Changes go through netsh using the interface index, so they work on disconnected
    /// adapters and on any Windows display language. Success is decided by reading the
    /// saved configuration back from the registry, never by parsing netsh text.
    /// </summary>
    public static class NetworkService
    {
        const string TcpipInterfaces = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\";
        const int WmiDeviceDisabled = 22;

        [DllImport("kernel32.dll")] static extern uint GetOEMCP();

        public static bool IsElevated()
        {
            using (var id = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }

        // ---------- Listing ----------

        public static List<AdapterInfo> ListAdapters()
        {
            List<AdapterInfo> list;
            try { list = ListFromWmi(); }
            catch (Exception ex)
            {
                Log.Write("WMI listing failed, using fallback: " + ex.Message);
                list = ListFromNetworkInterface();
            }
            return list.OrderBy(a => a.Status == LinkStatus.Connected ? 0 : 1)
                       .ThenBy(a => a.IsPhysical ? 0 : 1)
                       .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                       .ToList();
        }

        static List<AdapterInfo> ListFromWmi()
        {
            var list = new List<AdapterInfo>();
            const string query = "SELECT DeviceID,Name,NetConnectionID,GUID,InterfaceIndex,NetConnectionStatus," +
                                 "ConfigManagerErrorCode,PhysicalAdapter FROM Win32_NetworkAdapter WHERE NetConnectionID IS NOT NULL";
            using (var searcher = new ManagementObjectSearcher(query))
            using (var results = searcher.Get())
            {
                foreach (ManagementBaseObject o in results)
                {
                    using (o)
                    {
                        int connection = o["NetConnectionStatus"] == null ? 0 : Convert.ToInt32(o["NetConnectionStatus"]);
                        int deviceError = o["ConfigManagerErrorCode"] == null ? 0 : Convert.ToInt32(o["ConfigManagerErrorCode"]);
                        LinkStatus status;
                        if (deviceError == WmiDeviceDisabled) status = LinkStatus.Disabled;
                        else if (connection == 2) status = LinkStatus.Connected;
                        else if (connection == 1 || connection == 8 || connection == 9) status = LinkStatus.Connecting;
                        else status = LinkStatus.Disconnected;

                        list.Add(new AdapterInfo
                        {
                            Key = Convert.ToString(o["DeviceID"]),
                            Name = Convert.ToString(o["NetConnectionID"]),
                            Description = Convert.ToString(o["Name"]),
                            Guid = Convert.ToString(o["GUID"]),
                            Index = o["InterfaceIndex"] == null ? 0 : Convert.ToInt32(o["InterfaceIndex"]),
                            IsPhysical = o["PhysicalAdapter"] != null && (bool)o["PhysicalAdapter"],
                            Status = status
                        });
                    }
                }
            }
            return list;
        }

        static List<AdapterInfo> ListFromNetworkInterface()
        {
            var list = new List<AdapterInfo>();
            foreach (NetworkInterface n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    n.NetworkInterfaceType == NetworkInterfaceType.Tunnel || !n.Supports(NetworkInterfaceComponent.IPv4)) continue;
                list.Add(new AdapterInfo
                {
                    Key = n.Id, Name = n.Name, Description = n.Description, Guid = n.Id,
                    Index = n.GetIPProperties().GetIPv4Properties().Index,
                    IsPhysical = true,
                    Status = n.OperationalStatus == OperationalStatus.Up ? LinkStatus.Connected : LinkStatus.Disconnected
                });
            }
            return list;
        }

        // ---------- Reading ----------

        static List<string> Multi(RegistryKey key, string name)
        {
            object v = key.GetValue(name);
            IEnumerable<string> raw;
            if (v is string[]) raw = (string[])v;
            else if (v is string) raw = ((string)v).Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            else raw = new string[0];
            return raw.Select(s => s.Trim()).Where(s => s.Length > 0 && s != "0.0.0.0").ToList();
        }

        public static IpConfig ReadConfigured(string guid)
        {
            var config = IpConfig.Automatic();
            if (string.IsNullOrEmpty(guid)) return config;
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(TcpipInterfaces + guid))
            {
                if (key == null) return config;
                object dhcp = key.GetValue("EnableDHCP");
                config.Dhcp = dhcp == null || Convert.ToInt32(dhcp) != 0;
                config.Dns = Multi(key, "NameServer");
                if (!config.Dhcp)
                {
                    config.Ip = Multi(key, "IPAddress").FirstOrDefault() ?? "";
                    config.Mask = Multi(key, "SubnetMask").FirstOrDefault() ?? "";
                    config.Gateway = Multi(key, "DefaultGateway").FirstOrDefault() ?? "";
                }
            }
            return config;
        }

        static NetworkInterface FindLive(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return null;
            return NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => string.Equals(n.Id, guid, StringComparison.OrdinalIgnoreCase));
        }

        static UnicastIPAddressInformation LiveV4(NetworkInterface n)
        {
            return n == null ? null : n.GetIPProperties().UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
        }

        public static AdapterState ReadState(AdapterInfo adapter)
        {
            var state = new AdapterState { Configured = ReadConfigured(adapter.Guid) };
            IpConfig c = state.Configured;
            state.ShownIp = c.Ip; state.ShownMask = c.Mask; state.ShownGateway = c.Gateway;
            state.ShownDns = new List<string>(c.Dns);
            if (c.Dhcp) ReadLastLease(adapter.Guid, state);
            if (adapter.Status != LinkStatus.Connected) return state;
            try
            {
                NetworkInterface n = FindLive(adapter.Guid);
                if (n == null) return state;
                IPInterfaceProperties p = n.GetIPProperties();
                UnicastIPAddressInformation v4 = LiveV4(n);
                if (v4 != null)
                {
                    state.ShownIp = v4.Address.ToString();
                    if (v4.IPv4Mask != null) state.ShownMask = v4.IPv4Mask.ToString();
                }
                GatewayIPAddressInformation gw = p.GatewayAddresses
                    .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork);
                if (gw != null) state.ShownGateway = gw.Address.ToString();
                List<string> dns = p.DnsAddresses.Where(d => d.AddressFamily == AddressFamily.InterNetwork)
                                    .Select(d => d.ToString()).ToList();
                if (dns.Count > 0) state.ShownDns = dns;
            }
            catch (Exception ex) { Log.Write("live state read failed: " + ex.Message); }
            return state;
        }

        /// <summary>What the DHCP server last gave this adapter, kept by Windows even while unplugged.</summary>
        static void ReadLastLease(string guid, AdapterState state)
        {
            if (string.IsNullOrEmpty(guid)) return;
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(TcpipInterfaces + guid))
                {
                    if (key == null) return;
                    state.ShownIp = Multi(key, "DhcpIPAddress").FirstOrDefault() ?? "";
                    state.ShownMask = Multi(key, "DhcpSubnetMask").FirstOrDefault() ?? "";
                    state.ShownGateway = Multi(key, "DhcpDefaultGateway").FirstOrDefault() ?? "";
                    state.ShownDns = Multi(key, "DhcpNameServer");
                }
            }
            catch (Exception ex) { Log.Write("lease read failed: " + ex.Message); }
        }

        // ---------- Changing ----------

        public static bool EnableAdapter(AdapterInfo adapter, out string error)
        {
            error = null;
            try
            {
                string path = "Win32_NetworkAdapter.DeviceID=\"" + adapter.Key + "\"";
                using (var o = new ManagementObject(path))
                {
                    uint code = Convert.ToUInt32(o.InvokeMethod("Enable", null));
                    if (code == 0) return true;
                    error = "Windows refused to enable the adapter (code " + code + ").";
                }
            }
            catch (Exception ex) { error = ex.Message; }
            return false;
        }

        public static ApplyResult Apply(AdapterInfo requested, IpConfig target)
        {
            // The interface index can change after a driver reset, so never trust the cached one.
            AdapterInfo adapter = ListAdapters().FirstOrDefault(a => a.Key == requested.Key);
            if (adapter == null) return ApplyResult.Fail(requested.Name + " is no longer available. Press refresh.");
            if (adapter.Status == LinkStatus.Disabled) return ApplyResult.Fail(adapter.Name + " is disabled. Enable it first.");
            if (adapter.Index <= 0 || string.IsNullOrEmpty(adapter.Guid))
                return ApplyResult.Fail("Windows didn't report this adapter's details. Press refresh and try again.");

            IpConfig before = ReadConfigured(adapter.Guid);
            if (before.SameAs(target))
                return ApplyResult.Ok(adapter.Name + " already uses these settings.");

            Log.Write("apply on " + adapter.Name + " (" + adapter.Index + "): " + before + " -> " + target);
            string failure;
            if (!ApplyAndVerify(adapter, target, before, out failure))
            {
                Log.Write("apply failed: " + failure);
                IpConfig now = ReadConfigured(adapter.Guid);
                if (now.SameAs(before))
                    return ApplyResult.Fail("Couldn't change the settings: " + failure + " Nothing was changed.");
                string ignored;
                bool restored = ApplyAndVerify(adapter, before, now, out ignored);
                return ApplyResult.Fail("Couldn't change the settings: " + failure +
                    (restored ? " Your previous settings were restored." : " Check the adapter's settings in Windows."));
            }
            return Settle(adapter, target);
        }

        static bool ApplyAndVerify(AdapterInfo adapter, IpConfig target, IpConfig current, out string failure)
        {
            string name = adapter.Index.ToString(CultureInfo.InvariantCulture);
            string lastError = "";
            Action<string> run = args =>
            {
                string output;
                if (RunNetsh(args, out output) != 0 && output.Length > 0) lastError = output;
            };

            if (target.Dhcp)
            {
                // A leftover static default route would otherwise compete with the DHCP one.
                if (!current.Dhcp && current.Gateway.Length > 0)
                    run("interface ipv4 delete route prefix=0.0.0.0/0 interface=" + name + " nexthop=" + current.Gateway);
                run("interface ipv4 set address name=" + name + " source=dhcp");
            }
            else
            {
                string gateway = target.Gateway.Length > 0 ? "gateway=" + target.Gateway + " gwmetric=0" : "gateway=none";
                string setStatic = "interface ipv4 set address name=" + name + " source=static address=" + target.Ip +
                    " mask=" + target.Mask + " " + gateway;
                run(setStatic);

                // On an unplugged adapter Windows saves the address but silently stays in DHCP mode.
                // netsh reports success and Set-NetIPInterface has no effect, so clear the flag directly.
                if (StillDhcp(adapter.Guid) && ClearDhcpFlag(adapter.Guid)) run(setStatic);
            }

            if (target.Dns.Count == 0)
            {
                run(target.Dhcp ? "interface ipv4 set dnsservers name=" + name + " source=dhcp"
                                : "interface ipv4 set dnsservers name=" + name + " source=static address=none validate=no");
            }
            else
            {
                run("interface ipv4 set dnsservers name=" + name + " source=static address=" + target.Dns[0] + " register=primary validate=no");
                for (int i = 1; i < target.Dns.Count; i++)
                    run("interface ipv4 add dnsservers name=" + name + " address=" + target.Dns[i] +
                        " index=" + (i + 1).ToString(CultureInfo.InvariantCulture) + " validate=no");
            }

            // netsh also returns failure for harmless cases ("already enabled", "object exists"),
            // so the saved configuration is what decides.
            for (int attempt = 0; attempt < 10; attempt++)
            {
                if (ReadConfigured(adapter.Guid).SameAs(target)) { failure = null; return true; }
                Thread.Sleep(300);
            }
            failure = lastError.Length > 0 ? lastError : "Windows didn't save the new values.";
            return false;
        }

        static bool StillDhcp(string guid)
        {
            for (int i = 0; i < 4; i++)
            {
                if (!ReadConfigured(guid).Dhcp) return false;
                Thread.Sleep(200);
            }
            return true;
        }

        static bool ClearDhcpFlag(string guid)
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(TcpipInterfaces + guid, true))
                {
                    if (key == null) return false;
                    key.SetValue("EnableDHCP", 0, RegistryValueKind.DWord);
                    Log.Write("cleared DHCP flag on " + guid);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Write("could not clear DHCP flag: " + ex.Message);
                return false;
            }
        }

        /// <summary>Waits briefly for the live result so the message can say what actually happened.</summary>
        static ApplyResult Settle(AdapterInfo adapter, IpConfig target)
        {
            if (adapter.Status != LinkStatus.Connected)
                return ApplyResult.Ok("Saved. " + adapter.Name + " will use this when it connects.");

            if (target.Dhcp)
            {
                for (int i = 0; i < 30; i++)
                {
                    UnicastIPAddressInformation v4 = SafeLive(adapter);
                    string address = v4 == null ? null : v4.Address.ToString();
                    if (address != null && !address.StartsWith("169.254.") && SafeDad(v4) == DuplicateAddressDetectionState.Preferred)
                        return ApplyResult.Ok("Done. " + adapter.Name + " got " + address + " automatically.");
                    Thread.Sleep(500);
                }
                return ApplyResult.Warn("Automatic mode is on, but no DHCP server has answered yet. Windows will keep trying.");
            }

            for (int i = 0; i < 10; i++)
            {
                UnicastIPAddressInformation v4 = SafeLive(adapter);
                if (v4 != null && v4.Address.ToString() == target.Ip)
                {
                    DuplicateAddressDetectionState dad = SafeDad(v4);
                    if (dad == DuplicateAddressDetectionState.Duplicate)
                        return ApplyResult.Warn(target.Ip + " is already used by another device on this network. Pick a different IP.");
                    if (dad == DuplicateAddressDetectionState.Preferred) break;
                }
                Thread.Sleep(400);
            }
            return ApplyResult.Ok("Done. " + adapter.Name + " now uses " + target.Ip + ".");
        }

        static UnicastIPAddressInformation SafeLive(AdapterInfo adapter)
        {
            try { return LiveV4(FindLive(adapter.Guid)); }
            catch { return null; }
        }

        static DuplicateAddressDetectionState SafeDad(UnicastIPAddressInformation v4)
        {
            try { return v4.DuplicateAddressDetectionState; }
            catch { return DuplicateAddressDetectionState.Preferred; }
        }

        static int RunNetsh(string arguments, out string output)
        {
            output = "";
            try
            {
                Encoding oem = Encoding.GetEncoding((int)GetOEMCP());
                var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "netsh.exe"), arguments)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = oem, StandardErrorEncoding = oem
                };
                using (Process p = Process.Start(info))
                {
                    Task<string> stdout = p.StandardOutput.ReadToEndAsync();
                    Task<string> stderr = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(30000))
                    {
                        try { p.Kill(); } catch { }
                        output = "Windows took too long to respond.";
                        Log.Write("netsh " + arguments + " => timeout");
                        return -1;
                    }
                    Task.WaitAll(new Task[] { stdout, stderr }, 2000);
                    output = (stdout.Result + " " + stderr.Result).Trim();
                    output = string.Join(" ", output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
                    Log.Write("netsh " + arguments + " => " + p.ExitCode + " " + output);
                    return p.ExitCode;
                }
            }
            catch (Exception ex)
            {
                output = ex.Message;
                Log.Write("netsh " + arguments + " => exception " + ex);
                return -1;
            }
        }
    }
}
