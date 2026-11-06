using System;
using System.Collections.Generic;
using System.Globalization;

namespace EasyIP
{
    /// <summary>Pure IPv4 parsing and validation. No I/O, so it is fully unit tested.</summary>
    public static class IpValidator
    {
        public const string FieldIp = "ip", FieldMask = "mask", FieldGateway = "gateway", FieldDns1 = "dns1", FieldDns2 = "dns2";

        public static bool TryParse(string text, out uint value)
        {
            value = 0;
            if (text == null) return false;
            string[] parts = text.Trim().Split('.');
            if (parts.Length != 4) return false;
            foreach (string p in parts)
            {
                if (p.Length == 0 || p.Length > 3) return false;
                if (p.Length > 1 && p[0] == '0') return false;   // 010 is ambiguous (octal), reject it
                foreach (char c in p) if (c < '0' || c > '9') return false;
                int n = int.Parse(p, CultureInfo.InvariantCulture);
                if (n > 255) return false;
                value = (value << 8) | (uint)n;
            }
            return true;
        }

        public static string Format(uint v)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0}.{1}.{2}.{3}",
                (v >> 24) & 255, (v >> 16) & 255, (v >> 8) & 255, v & 255);
        }

        public static uint PrefixToMask(int prefix)
        {
            return prefix <= 0 ? 0u : uint.MaxValue << (32 - prefix);
        }

        public static bool IsContiguousMask(uint mask)
        {
            if (mask == 0) return false;
            uint inverted = ~mask;
            return (inverted & (inverted + 1)) == 0;
        }

        public static int MaskToPrefix(uint mask)
        {
            int n = 0;
            while ((mask & 0x80000000u) != 0) { n++; mask <<= 1; }
            return n;
        }

        /// <summary>Accepts 255.255.255.0, 24 or /24 and returns dotted form.</summary>
        public static bool TryNormalizeMask(string text, out string mask)
        {
            mask = null;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string t = text.Trim();
            if (t.StartsWith("/")) t = t.Substring(1);
            int prefix;
            if (t.IndexOf('.') < 0 && int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out prefix))
            {
                if (prefix < 1 || prefix > 32) return false;
                mask = Format(PrefixToMask(prefix));
                return true;
            }
            uint m;
            if (!TryParse(t, out m) || !IsContiguousMask(m)) return false;
            mask = Format(m);
            return true;
        }

        /// <summary>Splits "192.168.1.10/24" into address and mask.</summary>
        public static bool TrySplitCidr(string text, out string ip, out string mask)
        {
            ip = null; mask = null;
            if (text == null) return false;
            int slash = text.IndexOf('/');
            if (slash <= 0) return false;
            string left = text.Substring(0, slash).Trim();
            uint dummy;
            if (!TryParse(left, out dummy)) return false;
            if (!TryNormalizeMask(text.Substring(slash), out mask)) return false;
            ip = left;
            return true;
        }

        static bool IsUsableUnicast(uint a)
        {
            uint first = a >> 24;
            return first != 0 && first != 127 && first < 224;
        }

        /// <summary>Returns field -> message. Empty means the configuration is valid.</summary>
        public static Dictionary<string, string> Validate(string ip, string mask, string gateway, string dns1, string dns2)
        {
            var errors = new Dictionary<string, string>();
            uint ipv = 0, maskv = 0;
            bool ipOk = false, maskOk = false;

            if (string.IsNullOrWhiteSpace(ip)) errors[FieldIp] = "Enter an IP address";
            else if (!TryParse(ip, out ipv)) errors[FieldIp] = "Not a valid IP, example 192.168.1.10";
            else if (!IsUsableUnicast(ipv)) errors[FieldIp] = "This address can't be used on an adapter";
            else ipOk = true;

            string normalized;
            if (string.IsNullOrWhiteSpace(mask)) errors[FieldMask] = "Enter a subnet mask";
            else if (!TryNormalizeMask(mask, out normalized)) errors[FieldMask] = "Not a valid mask, example 255.255.255.0";
            else { TryParse(normalized, out maskv); maskOk = true; }

            bool subnetKnown = ipOk && maskOk;
            uint hostBits = ~maskv;
            bool hasHostRange = MaskToPrefix(maskv) <= 30;
            if (subnetKnown && hasHostRange)
            {
                if ((ipv & hostBits) == 0) errors[FieldIp] = "This is the network address, pick another";
                else if ((ipv & hostBits) == hostBits) errors[FieldIp] = "This is the broadcast address, pick another";
            }

            if (!string.IsNullOrWhiteSpace(gateway))
            {
                uint gw;
                if (!TryParse(gateway, out gw)) errors[FieldGateway] = "Not a valid IP address";
                else if (!IsUsableUnicast(gw)) errors[FieldGateway] = "This address can't be a gateway";
                else if (subnetKnown)
                {
                    if (gw == ipv) errors[FieldGateway] = "Gateway can't be the same as the IP";
                    else if ((gw & maskv) != (ipv & maskv)) errors[FieldGateway] = "Gateway must be in the same subnet as the IP";
                    else if (hasHostRange && ((gw & hostBits) == 0 || (gw & hostBits) == hostBits))
                        errors[FieldGateway] = "This is not a host address in the subnet";
                }
            }

            uint d1 = 0, d2 = 0;
            bool d1Set = !string.IsNullOrWhiteSpace(dns1), d2Set = !string.IsNullOrWhiteSpace(dns2);
            if (d1Set && (!TryParse(dns1, out d1) || d1 == 0 || d1 >> 24 >= 224)) errors[FieldDns1] = "Not a valid DNS address";
            if (d2Set && (!TryParse(dns2, out d2) || d2 == 0 || d2 >> 24 >= 224)) errors[FieldDns2] = "Not a valid DNS address";
            if (d1Set && d2Set && d1 == d2 && !errors.ContainsKey(FieldDns1) && !errors.ContainsKey(FieldDns2))
                errors[FieldDns2] = "Same as the preferred DNS";

            return errors;
        }
    }
}
