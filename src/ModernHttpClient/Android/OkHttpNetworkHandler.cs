using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using Square.OkHttp3;
using Javax.Net.Ssl;
using Java.IO;
using Java.Security;
using Java.Security.Cert;
using Android.OS;
using Android.Runtime;

namespace ModernHttpClient
{
    public class NativeMessageHandler : HttpClientHandler
    {
        readonly Lazy<OkHttpClient> client;
        readonly Lazy<CertificatePinner.Builder> certificatePinner;
        readonly CacheControl noCacheCacheControl;

        private KeyManagerFactory _keyMgrFactory;
        private TrustManagerFactory _trustMgrFactory;
        private IX509TrustManager _x509TrustManager;
        private IKeyManager[] KeyManagers => _keyMgrFactory?.GetKeyManagers();
        private ITrustManager[] TrustManagers => _trustMgrFactory?.GetTrustManagers();

        readonly Dictionary<HttpRequestMessage, WeakReference> registeredProgressCallbacks = new Dictionary<HttpRequestMessage, WeakReference>();
        readonly Dictionary<string, string> headerSeparators = new Dictionary<string, string>{
                {"User-Agent", " "}
        };

        public bool DisableCaching { get; set; }

        public NativeMessageHandler()
        {
            client = new Lazy<OkHttpClient>(GetClientInstance);
            certificatePinner = new Lazy<CertificatePinner.Builder>();
            noCacheCacheControl = new CacheControl.Builder().NoCache().Build();
        }

        public void RegisterForProgress(HttpRequestMessage request, ProgressDelegate callback)
        {
            if (callback == null && registeredProgressCallbacks.ContainsKey(request)) {
                registeredProgressCallbacks.Remove(request);
                return;
            }

            registeredProgressCallbacks[request] = new WeakReference(callback);
        }

        /// <summary>
        /// Add certificate pins for a given hostname (Android implementation)
        /// </summary>
        /// <param name="hostname">The hostname</param>
        /// <param name="pins">The array of certifiate pins (example of pin string: "sha256/fiKY8VhjQRb2voRmVXsqI0xPIREcwOVhpexrplrlqQY=")</param>
        public virtual void SetCertificatePinner(string hostname, string[] pins)
        {
            certificatePinner.Value.Add(hostname, pins);
        }

        /// <summary>
        /// Set certificates for the trusted Root Certificate Authorities (Android implementation)
        /// </summary>
        /// <param name="certificates">Certificates for the CAs to trust</param>
        public void SetTrustedCertificates(params byte[][] certificates)
        {
            if (certificates == null) {
                _trustMgrFactory = null;
                _x509TrustManager = null;
                return;
            }
            var keyStore = KeyStore.GetInstance(KeyStore.DefaultType);
            keyStore.Load(null);
            var certFactory = CertificateFactory.GetInstance("X.509");
            foreach (var certificate in certificates) {
                var cert = (X509Certificate)certFactory.GenerateCertificate(new System.IO.MemoryStream(certificate));
                keyStore.SetCertificateEntry(cert.SubjectDN.Name, cert);
            }

            _trustMgrFactory = TrustManagerFactory.GetInstance(TrustManagerFactory.DefaultAlgorithm);
            _trustMgrFactory.Init(keyStore);

            _keyMgrFactory = KeyManagerFactory.GetInstance("X509");
            _keyMgrFactory.Init(keyStore, null);

            foreach (var trustManager in TrustManagers) {
                _x509TrustManager = trustManager.JavaCast<IX509TrustManager>();
                if (_x509TrustManager != null) {
                    break;
                }
            }
        }

        private OkHttpClient GetClientInstance()
        {
            var builder = new OkHttpClient.Builder();

            if (certificatePinner.IsValueCreated) {
                builder.CertificatePinner(certificatePinner.Value.Build());
            }

            if (Build.VERSION.SdkInt < BuildVersionCodes.Lollipop) {
                // Support TLS1.2 on Android versions before Lollipop
                builder.SslSocketFactory(new TlsSocketFactory(KeyManagers, TrustManagers), _x509TrustManager ?? TlsSocketFactory.GetDefaultTrustManager());
            } else if (_keyMgrFactory != null || _trustMgrFactory != null) {
                var context = SSLContext.GetInstance("TLS");
                context.Init(KeyManagers, TrustManagers, null);
                builder.SslSocketFactory(context.SocketFactory, _x509TrustManager ?? TlsSocketFactory.GetDefaultTrustManager());
            }

            return builder.Build();
        }

        private ProgressDelegate GetAndRemoveCallbackFromRegister(HttpRequestMessage request)
        {
            ProgressDelegate emptyDelegate = delegate { };

            lock (registeredProgressCallbacks) {
                if (!registeredProgressCallbacks.ContainsKey(request))
                    return emptyDelegate;

                var weakRef = registeredProgressCallbacks[request];
                if (weakRef == null)
                    return emptyDelegate;

                if (!(weakRef.Target is ProgressDelegate callback))
                    return emptyDelegate;

                registeredProgressCallbacks.Remove(request);
                return callback;
            }
        }

        private string GetHeaderSeparator(string name)
        {
            if (headerSeparators.ContainsKey(name)) {
                return headerSeparators[name];
            }

            return ",";
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var java_uri = request.RequestUri.GetComponents(UriComponents.AbsoluteUri, UriFormat.UriEscaped);
            var url = new Java.Net.URL(java_uri);

            var body = default(RequestBody);
            if (request.Content != null) {
                var bytes = await request.Content.ReadAsByteArrayAsync().ConfigureAwait(false);

                var contentType = "text/plain";
                if (request.Content.Headers.ContentType != null) {
                    contentType = String.Join(" ", request.Content.Headers.GetValues("Content-Type"));
                }
                body = RequestBody.Create(MediaType.Parse(contentType), bytes);
            }

            var builder = new Request.Builder()
                .Method(request.Method.Method.ToUpperInvariant(), body)
                .Url(url);

            if (DisableCaching) {
                builder.CacheControl(noCacheCacheControl);
            }

            var keyValuePairs = request.Headers
                .Union(request.Content != null ?
                    request.Content.Headers :
                    Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>());

            foreach (var kvp in keyValuePairs)
                builder.AddHeader(kvp.Key, String.Join(GetHeaderSeparator(kvp.Key), kvp.Value));

            cancellationToken.ThrowIfCancellationRequested();

            var rq = builder.Build();
            var call = client.Value.NewCall(rq);

            // NB: Even closing a socket must be done off the UI thread. Cray!
            cancellationToken.Register(() => Task.Run(() => call.Cancel()));

            Response resp;
            try {
                resp = await call.ExecuteAsync().ConfigureAwait(false);
            } catch (IOException ex) {
                if (ex.Message != null) {
                    if (ex.Message.StartsWith("Certificate pinning failure", StringComparison.OrdinalIgnoreCase)) {
                        throw new WebException(ex.Message, WebExceptionStatus.TrustFailure);
                    } else if (ex.Message.ToLowerInvariant().Contains("canceled")) {
                        throw new System.OperationCanceledException();
                    }
                }
                throw;
            }

            var respBody = resp.Body();

            cancellationToken.ThrowIfCancellationRequested();

            var ret = new HttpResponseMessage((HttpStatusCode)resp.Code()) {
                RequestMessage = request,
                ReasonPhrase = resp.Message()
            };

            if (respBody != null) {
                ret.Content = new ProgressStreamContent(respBody.ByteStream(), CancellationToken.None) {
                    Progress = GetAndRemoveCallbackFromRegister(request)
                };
            } else {
                ret.Content = new ByteArrayContent(new byte[0]);
            }

            var respHeaders = resp.Headers();
            foreach (var k in respHeaders.Names()) {
                ret.Headers.TryAddWithoutValidation(k, respHeaders.Get(k));
                ret.Content.Headers.TryAddWithoutValidation(k, respHeaders.Get(k));
            }

            return ret;
        }
    }
}
