using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace ModernHttpClient
{
    public class CertificatePinner
    {
        readonly ConcurrentDictionary<string, string[]> _pins;

        public CertificatePinner()
        {
            _pins = new ConcurrentDictionary<string, string[]>();
        }

        public void AddPins(string hostname, string[] pins)
        {
            _pins[hostname] = pins;
        }

        public bool HasPin(string hostname)
        {
            return _pins.ContainsKey(hostname);
        }

        public bool Check(string hostname, byte[] derEncodedCertificate)
        {
            // Get pins
            if (!_pins.TryGetValue(hostname, out string[] pins)) {
                return true;
            }

            // Compute spki fingerprint
            string spkiFingerprint;
            using (var sha = SHA256.Create()) {
                var digest = sha.ComputeHash(derEncodedCertificate);
                spkiFingerprint = Convert.ToBase64String(digest);
            }

            // Check pin
            return Array.IndexOf(pins, spkiFingerprint) >= 0;
        }
    }
}
