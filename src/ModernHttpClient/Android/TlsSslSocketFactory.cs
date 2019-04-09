using System;
using Java.Interop;
using Java.Net;
using Java.Security;
using Javax.Net.Ssl;

namespace ModernHttpClient
{
    public class TlsSocketFactory : SSLSocketFactory
    {
        readonly SSLSocketFactory socketFactory;

        public TlsSocketFactory()
        {
            SSLContext context = SSLContext.GetInstance("TLS");
            context.Init(null, null, null);
            socketFactory = context.SocketFactory;

        }

        public TlsSocketFactory(IKeyManager[] keyManagers = null, ITrustManager[] trustManagers = null)
        {
            if (keyManagers != null || trustManagers != null) {
                var context = SSLContext.GetInstance("TLS");
                context.Init(keyManagers, trustManagers, null);
                socketFactory = context.SocketFactory;
            }
        }

        public override string[] GetDefaultCipherSuites() => socketFactory.GetDefaultCipherSuites();
        public override string[] GetSupportedCipherSuites() => socketFactory.GetSupportedCipherSuites();

        public override Socket CreateSocket()
        {
            var socket = (SSLSocket)socketFactory.CreateSocket();
            socket.SetEnabledProtocols(socket.GetSupportedProtocols());
            socket.SetEnabledCipherSuites(socket.GetSupportedCipherSuites());
            return socket;
        }

        public override Socket CreateSocket(string host, int port)
        {
            var socket = (SSLSocket)socketFactory.CreateSocket(host, port);
            socket.SetEnabledProtocols(socket.GetSupportedProtocols());
            socket.SetEnabledCipherSuites(socket.GetSupportedCipherSuites());
            return socket;
        }

        public override Socket CreateSocket(Socket s, string host, int port, bool autoClose)
        {
            var socket = (SSLSocket)socketFactory.CreateSocket(s, host, port, autoClose);
            socket.SetEnabledProtocols(socket.GetSupportedProtocols());
            socket.SetEnabledCipherSuites(socket.GetSupportedCipherSuites());
            return socket;
        }

        public override Socket CreateSocket(InetAddress address, int port, InetAddress localAddress, int localPort)
        {
            var socket = (SSLSocket)socketFactory.CreateSocket(address, port, localAddress, localPort);
            socket.SetEnabledProtocols(socket.GetSupportedProtocols());
            socket.SetEnabledCipherSuites(socket.GetSupportedCipherSuites());
            return socket;
        }

        public override Socket CreateSocket(InetAddress host, int port)
        {
            var socket = (SSLSocket)socketFactory.CreateSocket(host, port);
            socket.SetEnabledProtocols(socket.GetSupportedProtocols());
            socket.SetEnabledCipherSuites(socket.GetSupportedCipherSuites());
            return socket;
        }

        public override Socket CreateSocket(string host, int port, InetAddress localHost, int localPort)
        {
            var socket = (SSLSocket)socketFactory.CreateSocket(host, port, localHost, localPort);
            socket.SetEnabledProtocols(socket.GetSupportedProtocols());
            socket.SetEnabledCipherSuites(socket.GetSupportedCipherSuites());
            return socket;
        }

        public static IX509TrustManager GetDefaultTrustManager()
        {
            IX509TrustManager x509TrustManager = null;
            try {
                var trustManagerFactory = TrustManagerFactory.GetInstance(TrustManagerFactory.DefaultAlgorithm);
                trustManagerFactory.Init((KeyStore)null);
                foreach (var trustManager in trustManagerFactory.GetTrustManagers()) {
                    var manager = trustManager.JavaCast<IX509TrustManager>();
                    if (manager != null) {
                        x509TrustManager = manager;
                        break;
                    }
                }
            } catch (Exception ex) when (ex is NoSuchAlgorithmException || ex is KeyStoreException) {
                // move along...
            }
            return x509TrustManager;
        }
    }
}
