using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace EasyIP
{
    public enum LinkStatus { Connected, Connecting, Disconnected, Disabled }

    public class AdapterInfo
    {
        public string Key { get; set; }          // WMI DeviceID, stable while the adapter exists
        public string Name { get; set; }         // "Wi-Fi", "Ethernet"
        public string Description { get; set; }  // Hardware name
        public string Guid { get; set; }
        public int Index { get; set; }
        public LinkStatus Status { get; set; }
        public bool IsPhysical { get; set; }

        public string StatusText
        {
            get
            {
                switch (Status)
                {
                    case LinkStatus.Connected: return "Connected";
                    case LinkStatus.Connecting: return "Connecting";
                    case LinkStatus.Disabled: return "Disabled";
                    default: return "Not connected";
                }
            }
        }

        public string StatusColor
        {
            get
            {
                switch (Status)
                {
                    case LinkStatus.Connected: return "#16A34A";
                    case LinkStatus.Connecting: return "#D97706";
                    case LinkStatus.Disabled: return "#DC2626";
                    default: return "#9CA3AF";
                }
            }
        }

        public override string ToString() { return Name; }

        public string Subtitle { get { return StatusText + " · " + Description; } }

        public string Signature { get { return Key + "|" + Name + "|" + Guid + "|" + Index + "|" + Status; } }
    }

    /// <summary>What the adapter is configured to use. An empty DNS list on a DHCP config means automatic DNS.</summary>
    public class IpConfig
    {
        public bool Dhcp;
        public string Ip = "";
        public string Mask = "";
        public string Gateway = "";
        public List<string> Dns = new List<string>();

        public static IpConfig Automatic() { return new IpConfig { Dhcp = true }; }

        public bool SameAs(IpConfig o)
        {
            if (o == null || Dhcp != o.Dhcp || !Dns.SequenceEqual(o.Dns)) return false;
            return Dhcp || (Ip == o.Ip && Mask == o.Mask && Gateway == o.Gateway);
        }

        public override string ToString()
        {
            return Dhcp ? "DHCP dns=[" + string.Join(",", Dns) + "]"
                        : Ip + " " + Mask + " gw=" + Gateway + " dns=[" + string.Join(",", Dns) + "]";
        }
    }

    /// <summary>Configuration plus the values to show in the "current" card.</summary>
    public class AdapterState
    {
        public IpConfig Configured = IpConfig.Automatic();
        public string ShownIp = "", ShownMask = "", ShownGateway = "";
        public List<string> ShownDns = new List<string>();
    }

    public enum ApplyOutcome { Success, Warning, Failed }

    public class ApplyResult
    {
        public ApplyOutcome Outcome;
        public string Message;
        public static ApplyResult Ok(string m) { return new ApplyResult { Outcome = ApplyOutcome.Success, Message = m }; }
        public static ApplyResult Warn(string m) { return new ApplyResult { Outcome = ApplyOutcome.Warning, Message = m }; }
        public static ApplyResult Fail(string m) { return new ApplyResult { Outcome = ApplyOutcome.Failed, Message = m }; }
    }

    [DataContract]
    public class Profile
    {
        [DataMember] public string Name { get; set; }
        [DataMember] public string Ip { get; set; }
        [DataMember] public string Mask { get; set; }
        [DataMember] public string Gateway { get; set; }
        [DataMember] public string Dns1 { get; set; }
        [DataMember] public string Dns2 { get; set; }

        public override string ToString() { return Name; }
    }

    [DataContract]
    public class Settings
    {
        [DataMember] public string LastAdapter { get; set; }
        [DataMember] public List<Profile> Profiles { get; set; }
        /// <summary>Last address seen per adapter, Profile.Name holds the adapter name.</summary>
        [DataMember] public List<Profile> LastKnown { get; set; }

        public void Normalize()
        {
            if (Profiles == null) Profiles = new List<Profile>();
            if (LastKnown == null) LastKnown = new List<Profile>();
            Profiles.RemoveAll(p => p == null || string.IsNullOrWhiteSpace(p.Name));
            LastKnown.RemoveAll(p => p == null || string.IsNullOrWhiteSpace(p.Name));
        }
    }
}
