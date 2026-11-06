using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace EasyIP.Tests
{
    /// <summary>
    /// Run with no arguments for unit tests.
    /// Run elevated with: --integration "Adapter name" resultfile
    /// The integration run changes that adapter and restores it. Use a disconnected adapter.
    /// </summary>
    public static class Tests
    {
        static int passed, failed;
        static TextWriter output = Console.Out;

        static void Check(bool ok, string name)
        {
            if (ok) passed++;
            else { failed++; output.WriteLine("FAIL: " + name); }
        }

        static void Valid(string ip, string mask, string gw, string d1, string d2)
        {
            var e = IpValidator.Validate(ip, mask, gw, d1, d2);
            Check(e.Count == 0, "should be valid: " + ip + " " + mask + " " + gw + " " + d1 + " " + d2 + " -> " + string.Join(",", e.Keys));
        }

        static void Invalid(string field, string ip, string mask, string gw, string d1, string d2)
        {
            var e = IpValidator.Validate(ip, mask, gw, d1, d2);
            Check(e.ContainsKey(field), "should reject " + field + ": " + ip + " " + mask + " " + gw + " " + d1 + " " + d2);
        }

        static void Unit()
        {
            uint v;
            Check(IpValidator.TryParse("192.168.1.10", out v) && v == 0xC0A8010A, "parse");
            Check(IpValidator.TryParse(" 10.0.0.1 ", out v), "parse trims");
            foreach (string bad in new[] { "", "1.2.3", "1.2.3.4.5", "256.1.1.1", "1.2.3.a", "01.2.3.4", "1..2.3", "-1.2.3.4", "1.2.3.4 5", null })
                Check(!IpValidator.TryParse(bad, out v), "parse rejects " + bad);

            string m;
            Check(IpValidator.TryNormalizeMask("255.255.255.0", out m) && m == "255.255.255.0", "mask dotted");
            Check(IpValidator.TryNormalizeMask("24", out m) && m == "255.255.255.0", "mask 24");
            Check(IpValidator.TryNormalizeMask("/16", out m) && m == "255.255.0.0", "mask /16");
            Check(IpValidator.TryNormalizeMask("/32", out m) && m == "255.255.255.255", "mask /32");
            Check(IpValidator.TryNormalizeMask("255.255.255.252", out m), "mask /30");
            foreach (string bad in new[] { "255.0.255.0", "0.0.0.0", "33", "0", "/0", "abc", "255.255.255.1" })
                Check(!IpValidator.TryNormalizeMask(bad, out m), "mask rejects " + bad);

            string a, b;
            Check(IpValidator.TrySplitCidr("192.168.1.10/24", out a, out b) && a == "192.168.1.10" && b == "255.255.255.0", "cidr");
            Check(!IpValidator.TrySplitCidr("192.168.1.10", out a, out b), "no cidr");
            Check(!IpValidator.TrySplitCidr("192.168.1.10/99", out a, out b), "bad cidr");

            Valid("192.168.1.10", "255.255.255.0", "192.168.1.1", "8.8.8.8", "1.1.1.1");
            Valid("192.168.1.10", "255.255.255.0", "", "", "");
            Valid("10.1.2.3", "/8", "10.0.0.1", "", "8.8.8.8");
            Valid("172.16.5.1", "255.255.0.0", "172.16.0.1", "127.0.0.1", "");
            Valid("192.168.1.1", "255.255.255.254", "", "", "");
            Valid("192.168.1.0", "255.255.255.254", "", "", "");
            Valid("169.254.10.10", "255.255.0.0", "", "", "");

            Invalid("ip", "", "255.255.255.0", "", "", "");
            Invalid("ip", "abc", "255.255.255.0", "", "", "");
            Invalid("ip", "0.1.2.3", "255.255.255.0", "", "", "");
            Invalid("ip", "127.0.0.1", "255.255.255.0", "", "", "");
            Invalid("ip", "224.0.0.1", "255.255.255.0", "", "", "");
            Invalid("ip", "192.168.1.0", "255.255.255.0", "", "", "");
            Invalid("ip", "192.168.1.255", "255.255.255.0", "", "", "");
            Invalid("mask", "192.168.1.10", "", "", "", "");
            Invalid("mask", "192.168.1.10", "255.0.255.0", "", "", "");
            Invalid("gateway", "192.168.1.10", "255.255.255.0", "192.168.2.1", "", "");
            Invalid("gateway", "192.168.1.10", "255.255.255.0", "192.168.1.10", "", "");
            Invalid("gateway", "192.168.1.10", "255.255.255.0", "192.168.1.255", "", "");
            Invalid("gateway", "192.168.1.10", "255.255.255.0", "bad", "", "");
            Invalid("dns1", "192.168.1.10", "255.255.255.0", "", "bad", "");
            Invalid("dns1", "192.168.1.10", "255.255.255.0", "", "0.0.0.0", "");
            Invalid("dns2", "192.168.1.10", "255.255.255.0", "", "8.8.8.8", "8.8.8.8");
            Invalid("dns2", "192.168.1.10", "255.255.255.0", "", "", "239.0.0.1");

            var c1 = new IpConfig { Ip = "1.2.3.4", Mask = "255.0.0.0", Dns = new List<string> { "8.8.8.8" } };
            var c2 = new IpConfig { Ip = "1.2.3.4", Mask = "255.0.0.0", Dns = new List<string> { "8.8.8.8" } };
            Check(c1.SameAs(c2), "same config");
            c2.Gateway = "1.0.0.1";
            Check(!c1.SameAs(c2), "gateway differs");
            Check(IpConfig.Automatic().SameAs(IpConfig.Automatic()), "same dhcp");
            Check(!IpConfig.Automatic().SameAs(c1), "dhcp vs static");

            // Storage
            string dir = Path.Combine(Path.GetTempPath(), "easyip-test-" + Guid.NewGuid().ToString("N"));
            Storage.FolderOverride = dir;
            Check(Storage.Load().Profiles.Count == 0, "missing file gives empty settings");
            var s = Storage.Load();
            s.LastAdapter = "7";
            s.Profiles.Add(new Profile { Name = "Lab é", Ip = "10.0.0.5", Mask = "255.0.0.0", Gateway = "", Dns1 = null, Dns2 = "" });
            Check(Storage.Save(s), "save first");
            Check(Storage.Save(s), "save over existing");
            var r = Storage.Load();
            Check(r.LastAdapter == "7" && r.Profiles.Count == 1 && r.Profiles[0].Name == "Lab é" && r.Profiles[0].Ip == "10.0.0.5", "round trip");
            // A file written by an older version must still load.
            string old = File.ReadAllText(Path.Combine(dir, "settings.xml")).Replace("LastKnown", "LastStatic");
            File.WriteAllText(Path.Combine(dir, "settings.xml"), old);
            var o = Storage.Load();
            Check(o.Profiles.Count == 1 && o.LastKnown != null, "older settings file loads");
            File.WriteAllText(Path.Combine(dir, "settings.xml"), "<not valid");
            Check(Storage.Load().Profiles.Count == 0, "corrupt file gives empty settings");
            Check(File.Exists(Path.Combine(dir, "settings.xml.bad")), "corrupt file kept");
            Directory.Delete(dir, true);
            Storage.FolderOverride = null;

            // Read-only system probes
            var list = NetworkService.ListAdapters();
            Check(list.Count > 0, "adapters listed");
            foreach (AdapterInfo ad in list)
            {
                Check(!string.IsNullOrEmpty(ad.Name), "adapter has name");
                Check(NetworkService.ReadState(ad) != null, "state readable " + ad.Name);
            }
        }

        static void Step(AdapterInfo adapter, string title, IpConfig target)
        {
            ApplyResult r = NetworkService.Apply(adapter, target);
            IpConfig now = NetworkService.ReadConfigured(adapter.Guid);
            bool ok = r.Outcome != ApplyOutcome.Failed && now.SameAs(target);
            output.WriteLine((ok ? "ok   " : "FAIL ") + title + " | " + r.Outcome + " | " + r.Message + " | now: " + now);
            Check(ok, title);
        }

        static IpConfig Static(string ip, string mask, string gw, params string[] dns)
        {
            return new IpConfig { Dhcp = false, Ip = ip, Mask = mask, Gateway = gw, Dns = dns.ToList() };
        }

        /// <summary>Restarts the adapter so Windows reloads its saved settings, which also exercises disabled detection and Enable.</summary>
        static void Survives(AdapterInfo adapter)
        {
            IpConfig expected = NetworkService.ReadConfigured(adapter.Guid);
            using (var o = new System.Management.ManagementObject("Win32_NetworkAdapter.DeviceID=\"" + adapter.Key + "\""))
                o.InvokeMethod("Disable", null);
            AdapterInfo off = null;
            for (int i = 0; i < 20; i++)
            {
                off = NetworkService.ListAdapters().FirstOrDefault(a => a.Key == adapter.Key);
                if (off != null && off.Status == LinkStatus.Disabled) break;
                Thread.Sleep(500);
            }
            bool seen = off != null && off.Status == LinkStatus.Disabled;
            output.WriteLine((seen ? "ok   " : "FAIL ") + "disabled adapter detected, guid=" + (off == null ? "" : off.Guid) + " index=" + (off == null ? 0 : off.Index));
            Check(seen, "disabled detected");
            ApplyResult blocked = NetworkService.Apply(adapter, IpConfig.Automatic());
            output.WriteLine("apply while disabled: " + blocked.Outcome + " | " + blocked.Message);
            Check(blocked.Outcome == ApplyOutcome.Failed, "apply refused while disabled");

            string error;
            bool enabled = NetworkService.EnableAdapter(off ?? adapter, out error);
            output.WriteLine((enabled ? "ok   " : "FAIL ") + "enable | " + error);
            Check(enabled, "enable");
            AdapterInfo on = null;
            for (int i = 0; i < 30; i++)
            {
                on = NetworkService.ListAdapters().FirstOrDefault(a => a.Key == adapter.Key);
                if (on != null && on.Status != LinkStatus.Disabled && !string.IsNullOrEmpty(on.Guid)) break;
                Thread.Sleep(500);
            }
            Thread.Sleep(2000);
            IpConfig now = NetworkService.ReadConfigured(on.Guid);
            bool same = now.SameAs(expected);
            output.WriteLine((same ? "ok   " : "FAIL ") + "settings survive adapter restart | now: " + now);
            Check(same, "survives restart");
        }

        static void Integration(string adapterName)
        {
            AdapterInfo adapter = NetworkService.ListAdapters().FirstOrDefault(a => a.Name == adapterName);
            if (adapter == null) { output.WriteLine("FAIL adapter not found"); failed++; return; }
            output.WriteLine("adapter " + adapter.Name + " status " + adapter.Status + " elevated " + NetworkService.IsElevated());
            IpConfig original = NetworkService.ReadConfigured(adapter.Guid);
            output.WriteLine("original: " + original);
            try
            {
                Step(adapter, "static full", Static("10.99.1.10", "255.255.255.0", "10.99.1.1", "8.8.8.8", "1.1.1.1"));
                Step(adapter, "same settings again", Static("10.99.1.10", "255.255.255.0", "10.99.1.1", "8.8.8.8", "1.1.1.1"));
                Step(adapter, "new ip same gateway", Static("10.99.1.11", "255.255.255.0", "10.99.1.1", "8.8.8.8", "1.1.1.1"));
                Step(adapter, "one dns", Static("10.99.1.11", "255.255.255.0", "10.99.1.1", "9.9.9.9"));
                Step(adapter, "new gateway", Static("10.99.1.11", "255.255.255.0", "10.99.1.254", "9.9.9.9"));
                Step(adapter, "no gateway no dns", Static("172.31.7.9", "255.255.0.0", ""));
                Step(adapter, "dhcp from no gateway", IpConfig.Automatic());
                Step(adapter, "dhcp again", IpConfig.Automatic());
                Step(adapter, "static from dhcp", Static("10.99.2.10", "255.255.255.0", "10.99.2.1", "8.8.4.4"));
                Step(adapter, "dhcp from gateway", IpConfig.Automatic());
                Step(adapter, "static after dhcp keeps gateway", Static("10.99.2.10", "255.255.255.0", "10.99.2.1", "8.8.4.4"));
                Survives(adapter);
                adapter = NetworkService.ListAdapters().First(a => a.Name == adapterName);
                Step(adapter, "dhcp after restart", IpConfig.Automatic());
                Step(adapter, "static no gateway from dhcp", Static("10.99.3.10", "255.255.255.0", ""));
                Step(adapter, "dhcp final", IpConfig.Automatic());
            }
            catch (Exception ex) { failed++; output.WriteLine("FAIL exception " + ex); }
            finally
            {
                ApplyResult restore = NetworkService.Apply(adapter, original);
                bool ok = NetworkService.ReadConfigured(adapter.Guid).SameAs(original);
                output.WriteLine((ok ? "ok   " : "FAIL ") + "restored | " + restore.Message + " | now: " + NetworkService.ReadConfigured(adapter.Guid));
                Check(ok, "restored");
            }
        }

        static bool Reaches(string host)
        {
            for (int i = 0; i < 10; i++)
            {
                try
                {
                    using (var ping = new System.Net.NetworkInformation.Ping())
                        if (ping.Send(host, 1000).Status == System.Net.NetworkInformation.IPStatus.Success) return true;
                }
                catch { }
                Thread.Sleep(1000);
            }
            return false;
        }

        /// <summary>Connected adapter on DHCP: pins its own current address as static, checks the network, returns to DHCP.</summary>
        static void Live(string adapterName)
        {
            AdapterInfo adapter = NetworkService.ListAdapters().FirstOrDefault(a => a.Name == adapterName);
            if (adapter == null || adapter.Status != LinkStatus.Connected) { failed++; output.WriteLine("FAIL adapter not connected"); return; }
            AdapterState state = NetworkService.ReadState(adapter);
            IpConfig original = state.Configured;
            output.WriteLine("live start: " + state.ShownIp + " gw " + state.ShownGateway + " configured " + original);
            if (!original.Dhcp) { failed++; output.WriteLine("FAIL expected DHCP adapter"); return; }
            string gw = state.ShownGateway;
            try
            {
                Step(adapter, "live static same address", Static(state.ShownIp, state.ShownMask, gw, state.ShownDns.ToArray()));
                bool up = Reaches(gw);
                output.WriteLine((up ? "ok   " : "FAIL ") + "gateway reachable on static");
                Check(up, "gateway on static");
                AdapterState s2 = NetworkService.ReadState(adapter);
                output.WriteLine("live on static: " + s2.ShownIp + " gw " + s2.ShownGateway + " dns " + string.Join(",", s2.ShownDns));
            }
            catch (Exception ex) { failed++; output.WriteLine("FAIL exception " + ex); }
            finally
            {
                Step(adapter, "live back to dhcp", original);
                bool up = Reaches(gw);
                output.WriteLine((up ? "ok   " : "FAIL ") + "gateway reachable on dhcp");
                Check(up, "gateway on dhcp");
                AdapterState s3 = NetworkService.ReadState(adapter);
                output.WriteLine("live end: " + s3.ShownIp + " gw " + s3.ShownGateway + " dns " + string.Join(",", s3.ShownDns));
            }
        }

        public static int Main(string[] args)
        {
            StreamWriter file = null;
            if (args.Length >= 3 && args[0].StartsWith("--"))
            {
                file = new StreamWriter(args[2]) { AutoFlush = true };
                output = file;
                if (args[0] == "--live") Live(args[1]);
                else Integration(args[1]);
            }
            else Unit();
            output.WriteLine("passed " + passed + ", failed " + failed);
            if (file != null) file.Dispose();
            Thread.Sleep(50);
            return failed == 0 ? 0 : 1;
        }
    }
}
