using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using System.Threading;
using System.IO;
using System.Net;

namespace ModernHttpClient
{
    public class NativeMessageHandler : HttpClientHandler
    {
        const string wrongVersion = "You're referencing the Portable version in your App - you need to reference the platform (iOS/Android) version";

        public bool DisableCaching { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="T:ModernHttpClient.NativeMessageHandler"/> class.
        /// </summary>
        public NativeMessageHandler(): base()
        {
        }

        public void SetCertificatePinner(string hostname, string[] pins)
        {
        }

        public void SetTrustedCertificates(params byte[][] certificates)
        {
        }

        public void RegisterForProgress(HttpRequestMessage request, ProgressDelegate callback)
        {
            throw new Exception(wrongVersion);
        }
    }

    public class ProgressStreamContent : StreamContent 
    {
        const string wrongVersion = "You're referencing the Portable version in your App - you need to reference the platform (iOS/Android) version";

        ProgressStreamContent(Stream stream) : base(stream)
        {
            throw new Exception(wrongVersion);
        }

        ProgressStreamContent(Stream stream, int bufferSize) : base(stream, bufferSize)
        {
            throw new Exception(wrongVersion);
        }

        public ProgressDelegate Progress {
            get { throw new Exception(wrongVersion); }
            set { throw new Exception(wrongVersion); }
        }
    }

    public delegate void ProgressDelegate(long bytes, long totalBytes, long totalBytesExpected);

    public class NativeCookieHandler
    {
        const string wrongVersion = "You're referencing the Portable version in your App - you need to reference the platform (iOS/Android) version";

        public void SetCookies(IEnumerable<Cookie> cookies)
        {
            throw new Exception(wrongVersion);
        }

        public List<Cookie> Cookies {
            get { throw new Exception(wrongVersion); }
        }
    }
}
