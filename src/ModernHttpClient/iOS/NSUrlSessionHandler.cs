using System;
using System.Net.Http;
using System.Threading.Tasks;
using System.Threading;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using ModernHttpClient.CoreFoundation;
using ModernHttpClient.Foundation;

#if UNIFIED
using Foundation;
using Security;
#else
using MonoTouch.Foundation;
using MonoTouch.Security;
using System.Globalization;
#endif

namespace ModernHttpClient
{
    class InflightOperation
    {
        public HttpRequestMessage Request { get; set; }
        public TaskCompletionSource<HttpResponseMessage> FutureResponse { get; set; }
        public ProgressDelegate Progress { get; set; }
        public ByteArrayListStream ResponseBody { get; set; }
        public CancellationToken CancellationToken { get; set; }
        public bool IsCompleted { get; set; }
    }

    public class NativeMessageHandler : HttpClientHandler
    {
        readonly Lazy<NSUrlSession> session;
        readonly Lazy<CertificatePinner> certificatePinner;
        private X509Certificate2Collection trustedCertificates;

        readonly Dictionary<NSUrlSessionTask, InflightOperation> inflightRequests =
            new Dictionary<NSUrlSessionTask, InflightOperation>();

        readonly Dictionary<HttpRequestMessage, ProgressDelegate> registeredProgressCallbacks =
            new Dictionary<HttpRequestMessage, ProgressDelegate>();

        readonly Dictionary<string, string> headerSeparators =
            new Dictionary<string, string>(){
                {"User-Agent", " "}
            };

        public bool DisableCaching { get; set; }

        public NativeMessageHandler()
        {
            session = new Lazy<NSUrlSession>(GetSessionInstance);
            certificatePinner = new Lazy<CertificatePinner>(() => new CertificatePinner());
            var configuration = NSUrlSessionConfiguration.DefaultSessionConfiguration;

            // System.Net.ServicePointManager.SecurityProtocol provides a mechanism for specifying supported protocol types
            // for System.Net. Since iOS only provides an API for a minimum and maximum protocol we are not able to port
            // this configuration directly and instead use the specified minimum value when one is specified.
            var sp = ServicePointManager.SecurityProtocol;
            if ((sp & SecurityProtocolType.Ssl3) != 0)
                configuration.TLSMinimumSupportedProtocol = SslProtocol.Ssl_3_0;
            else if ((sp & SecurityProtocolType.Tls) != 0)
                configuration.TLSMinimumSupportedProtocol = SslProtocol.Tls_1_0;
            else if ((sp & SecurityProtocolType.Tls11) != 0)
                configuration.TLSMinimumSupportedProtocol = SslProtocol.Tls_1_1;
            else if ((sp & SecurityProtocolType.Tls12) != 0)
                configuration.TLSMinimumSupportedProtocol = SslProtocol.Tls_1_2;

            this.DisableCaching = false;
        }

        private NSUrlSession GetSessionInstance()
        {
            INSUrlSessionDelegate sessionDelegate = new DataTaskDelegate(this, certificatePinner.IsValueCreated ? certificatePinner.Value : null, trustedCertificates);
            return NSUrlSession.FromConfiguration(NSUrlSessionConfiguration.DefaultSessionConfiguration, sessionDelegate, null);
        }

        /// <summary>
        /// Set certificates for the trusted Root Certificate Authorities (iOS implementation)
        /// </summary>
        /// <param name="certificates">Certificates for the CAs to trust</param>
        public void SetTrustedCertificates(params byte[][] certificates)
        {
            if (certificates.Length == 0) {
                trustedCertificates = null;
                return;
            }
            trustedCertificates = new X509Certificate2Collection();
            foreach (var cert in certificates) {
                trustedCertificates.Import(cert);
            }
        }

        /// <summary>
        /// Add certificate pins for a given hostname (iOS implementation)
        /// </summary>
        /// <param name="hostname">The hostname</param>
        /// <param name="pins">The array of certifiate pins (example of pin string: "sha256/fiKY8VhjQRb2voRmVXsqI0xPIREcwOVhpexrplrlqQY=")</param>
        public void SetCertificatePinner(string hostname, string[] pins)
        {
            certificatePinner.Value.AddPins(hostname, pins);
        }

        private string GetHeaderSeparator(string name)
        {
            if (headerSeparators.ContainsKey(name)) {
                return headerSeparators[name];
            }

            return ",";
        }

        public void RegisterForProgress(HttpRequestMessage request, ProgressDelegate callback)
        {
            if (callback == null && registeredProgressCallbacks.ContainsKey(request)) {
                registeredProgressCallbacks.Remove(request);
                return;
            }

            registeredProgressCallbacks[request] = callback;
        }

        private ProgressDelegate GetAndRemoveCallbackFromRegister(HttpRequestMessage request)
        {
            ProgressDelegate emptyDelegate = delegate { };

            lock (registeredProgressCallbacks) {
                if (!registeredProgressCallbacks.ContainsKey(request)) return emptyDelegate;

                var callback = registeredProgressCallbacks[request];
                registeredProgressCallbacks.Remove(request);
                return callback;
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers as IEnumerable<KeyValuePair<string, IEnumerable<string>>>;
            var ms = new MemoryStream();

            if (request.Content != null) {
                await request.Content.CopyToAsync(ms).ConfigureAwait(false);
                headers = headers.Union(request.Content.Headers).ToArray();
            }

            var rq = new NSMutableUrlRequest() {
                AllowsCellularAccess = true,
                Body = NSData.FromArray(ms.ToArray()),
                CachePolicy = (!this.DisableCaching ? NSUrlRequestCachePolicy.UseProtocolCachePolicy : NSUrlRequestCachePolicy.ReloadIgnoringCacheData),
                Headers = headers.Aggregate(new NSMutableDictionary(), (acc, x) => {
                    acc.Add(new NSString(x.Key), new NSString(String.Join(getHeaderSeparator(x.Key), x.Value)));
                    return acc;
                }),
                HttpMethod = request.Method.ToString().ToUpperInvariant(),
                Url = NSUrl.FromString(request.RequestUri.AbsoluteUri),
            };

            var op = session.Value.CreateDataTask(rq);

            cancellationToken.ThrowIfCancellationRequested();

            var ret = new TaskCompletionSource<HttpResponseMessage>();
            cancellationToken.Register(() => ret.TrySetCanceled());

            lock (inflightRequests) {
                inflightRequests[op] = new InflightOperation() {
                    FutureResponse = ret,
                    Request = request,
                    Progress = getAndRemoveCallbackFromRegister(request),
                    ResponseBody = new ByteArrayListStream(),
                    CancellationToken = cancellationToken,
                };
            }

            op.Resume();
            return await ret.Task.ConfigureAwait(false);
        }

        class DataTaskDelegate : NSUrlSessionDataDelegate
        {
            NativeMessageHandler This { get; set; }
            readonly CertificatePinner _certificatePinner;
            readonly X509Certificate2Collection _trustedCertificates;

            public DataTaskDelegate(NativeMessageHandler that, CertificatePinner certificatePinner, X509Certificate2Collection trustedRoots)
            {
                this.This = that;
                _certificatePinner = certificatePinner;
                _trustedCertificates = trustedRoots;
            }

            public override void DidReceiveResponse(NSUrlSession session, NSUrlSessionDataTask dataTask, NSUrlResponse response, Action<NSUrlSessionResponseDisposition> completionHandler)
            {
                var data = GetResponseForTask(dataTask);

                try {
                    if (data.CancellationToken.IsCancellationRequested) {
                        dataTask.Cancel();
                    }

                    var resp = (NSHttpUrlResponse)response;
                    var req = data.Request;

                    var content = new CancellableStreamContent(data.ResponseBody, () => {
                        if (!data.IsCompleted) {
                            dataTask.Cancel();
                        }
                        data.IsCompleted = true;
                        data.ResponseBody.SetException(new OperationCanceledException());
                    }) {
                        Progress = data.Progress
                    };

                    // NB: The double cast is because of a Xamarin compiler bug
                    int status = (int)resp.StatusCode;
                    var ret = new HttpResponseMessage((HttpStatusCode)status) {
                        Content = content,
                        RequestMessage = data.Request,
                    };
                    ret.RequestMessage.RequestUri = new Uri(resp.Url.AbsoluteString);

                    foreach (var v in resp.AllHeaderFields) {
                        // NB: Cocoa trolling us so hard by giving us back dummy
                        // dictionary entries
                        if (v.Key == null || v.Value == null) continue;

                        ret.Headers.TryAddWithoutValidation(v.Key.ToString(), v.Value.ToString());
                        ret.Content.Headers.TryAddWithoutValidation(v.Key.ToString(), v.Value.ToString());
                    }

                    data.FutureResponse.TrySetResult(ret);
                } catch (Exception ex) {
                    data.FutureResponse.TrySetException(ex);
                }

                completionHandler(NSUrlSessionResponseDisposition.Allow);
            }

            public override void WillCacheResponse(NSUrlSession session, NSUrlSessionDataTask dataTask,
                NSCachedUrlResponse proposedResponse, Action<NSCachedUrlResponse> completionHandler)
            {
                completionHandler(This.DisableCaching ? null : proposedResponse);
            }

            public override void DidCompleteWithError(NSUrlSession session, NSUrlSessionTask task, NSError error)
            {
                var data = GetResponseForTask(task);
                data.IsCompleted = true;

                if (error != null) {
                    var ex = CreateExceptionForNSError(error);

                    // Pass the exception to the response
                    data.FutureResponse.TrySetException(ex);
                    data.ResponseBody.SetException(ex);
                    return;
                }

                data.ResponseBody.Complete();

                lock (This.inflightRequests) {
                    This.inflightRequests.Remove(task);
                }
            }

            public override void DidReceiveData(NSUrlSession session, NSUrlSessionDataTask dataTask, NSData byteData)
            {
                var data = GetResponseForTask(dataTask);
                var bytes = byteData.ToArray();

                // NB: If we're cancelled, we still might have one more chunk 
                // of data that attempts to be delivered
                if (data.IsCompleted) return;

                data.ResponseBody.AddByteArray(bytes);
            }

            private InflightOperation GetResponseForTask(NSUrlSessionTask task)
            {
                lock (This.inflightRequests) {
                    return This.inflightRequests[task];
                }
            }

            public override void DidReceiveChallenge(NSUrlSession session, NSUrlSessionTask task, NSUrlAuthenticationChallenge challenge, Action<NSUrlSessionAuthChallengeDisposition, NSUrlCredential> completionHandler)
            {
                if (challenge.ProtectionSpace.AuthenticationMethod == NSUrlProtectionSpace.AuthenticationMethodNTLM) {
                    NetworkCredential credentialsToUse;

                    if (This.Credentials != null) {
                        if (This.Credentials is NetworkCredential) {
                            credentialsToUse = (NetworkCredential)This.Credentials;
                        } else {
                            var uri = GetResponseForTask(task).Request.RequestUri;
                            credentialsToUse = This.Credentials.GetCredential(uri, "NTLM");
                        }
                        var credential = new NSUrlCredential(credentialsToUse.UserName, credentialsToUse.Password, NSUrlCredentialPersistence.ForSession);
                        completionHandler(NSUrlSessionAuthChallengeDisposition.UseCredential, credential);
                    }
                    return;
                }

                if (challenge.ProtectionSpace.AuthenticationMethod == NSUrlProtectionSpace.AuthenticationMethodServerTrust) {
                    challenge.ProtectionSpace.ServerSecTrust.SetAnchorCertificates(_trustedCertificates);

                    string hostname = task.CurrentRequest.Url.Host;
                    if (_certificatePinner != null && _certificatePinner.HasPin(hostname)) {
                        var serverTrust = challenge.ProtectionSpace.ServerSecTrust;
                        var status = serverTrust.Evaluate();
                        if (status == SecTrustResult.Proceed || status == SecTrustResult.Unspecified) {
                            var serverCertificate = serverTrust[0];
                            var x509Certificate = serverCertificate.ToX509Certificate2();
                            var derEncoded = x509Certificate.Export(X509ContentType.Cert);
                            if (_certificatePinner.Check(hostname, derEncoded)) {
                                completionHandler(NSUrlSessionAuthChallengeDisposition.UseCredential, NSUrlCredential.FromTrust(serverTrust));
                            } else {
                                completionHandler(NSUrlSessionAuthChallengeDisposition.CancelAuthenticationChallenge, null);
                            }
                            return;
                        }
                    }
                }

                completionHandler(NSUrlSessionAuthChallengeDisposition.PerformDefaultHandling, challenge.ProposedCredential);

            }

            public override void WillPerformHttpRedirection(NSUrlSession session, NSUrlSessionTask task, NSHttpUrlResponse response, NSUrlRequest newRequest, Action<NSUrlRequest> completionHandler)
            {
                NSUrlRequest nextRequest = (This.AllowAutoRedirect ? newRequest : null);
                completionHandler(nextRequest);
            }

            private static Exception CreateExceptionForNSError(NSError error)
            {
                var ret = default(Exception);
                var webExceptionStatus = WebExceptionStatus.UnknownError;

                var innerException = new NSErrorException(error);

                if (error.Domain == NSError.NSUrlErrorDomain) {
                    // Convert the error code into an enumeration (this is future
                    // proof, rather than just casting integer)
                    NSUrlErrorExtended urlError;
                    if (!Enum.TryParse<NSUrlErrorExtended>(error.Code.ToString(), out urlError)) urlError = NSUrlErrorExtended.Unknown;

                    // Parse the enum into a web exception status or exception. Some
                    // of these values don't necessarily translate completely to
                    // what WebExceptionStatus supports, so made some best guesses
                    // here.  For your reading pleasure, compare these:
                    //
                    // Apple docs: https://developer.apple.com/library/mac/documentation/Cocoa/Reference/Foundation/Miscellaneous/Foundation_Constants/index.html#//apple_ref/doc/constant_group/URL_Loading_System_Error_Codes
                    // .NET docs: http://msdn.microsoft.com/en-us/library/system.net.webexceptionstatus(v=vs.110).aspx
                    switch (urlError) {
                        case NSUrlErrorExtended.Cancelled:
                        case NSUrlErrorExtended.UserCancelledAuthentication:
                            // No more processing is required so just return.
                            return new OperationCanceledException(error.LocalizedDescription, innerException);
                        case NSUrlErrorExtended.BadURL:
                        case NSUrlErrorExtended.UnsupportedURL:
                        case NSUrlErrorExtended.CannotConnectToHost:
                        case NSUrlErrorExtended.ResourceUnavailable:
                        case NSUrlErrorExtended.NotConnectedToInternet:
                        case NSUrlErrorExtended.UserAuthenticationRequired:
                        case NSUrlErrorExtended.InternationalRoamingOff:
                        case NSUrlErrorExtended.CallIsActive:
                        case NSUrlErrorExtended.DataNotAllowed:
                            webExceptionStatus = WebExceptionStatus.ConnectFailure;
                            break;
                        case NSUrlErrorExtended.TimedOut:
                            webExceptionStatus = WebExceptionStatus.Timeout;
                            break;
                        case NSUrlErrorExtended.CannotFindHost:
                        case NSUrlErrorExtended.DNSLookupFailed:
                            webExceptionStatus = WebExceptionStatus.NameResolutionFailure;
                            break;
                        case NSUrlErrorExtended.DataLengthExceedsMaximum:
                            webExceptionStatus = WebExceptionStatus.MessageLengthLimitExceeded;
                            break;
                        case NSUrlErrorExtended.NetworkConnectionLost:
                            webExceptionStatus = WebExceptionStatus.ConnectionClosed;
                            break;
                        case NSUrlErrorExtended.HTTPTooManyRedirects:
                        case NSUrlErrorExtended.RedirectToNonExistentLocation:
                            webExceptionStatus = WebExceptionStatus.ProtocolError;
                            break;
                        case NSUrlErrorExtended.RequestBodyStreamExhausted:
                            webExceptionStatus = WebExceptionStatus.SendFailure;
                            break;
                        case NSUrlErrorExtended.BadServerResponse:
                        case NSUrlErrorExtended.ZeroByteResource:
                        case NSUrlErrorExtended.CannotDecodeRawData:
                        case NSUrlErrorExtended.CannotDecodeContentData:
                        case NSUrlErrorExtended.CannotParseResponse:
                        case NSUrlErrorExtended.FileDoesNotExist:
                        case NSUrlErrorExtended.FileIsDirectory:
                        case NSUrlErrorExtended.NoPermissionsToReadFile:
                        case NSUrlErrorExtended.CannotLoadFromNetwork:
                        case NSUrlErrorExtended.CannotCreateFile:
                        case NSUrlErrorExtended.CannotOpenFile:
                        case NSUrlErrorExtended.CannotCloseFile:
                        case NSUrlErrorExtended.CannotWriteToFile:
                        case NSUrlErrorExtended.CannotRemoveFile:
                        case NSUrlErrorExtended.CannotMoveFile:
                        case NSUrlErrorExtended.DownloadDecodingFailedMidStream:
                        case NSUrlErrorExtended.DownloadDecodingFailedToComplete:
                            webExceptionStatus = WebExceptionStatus.ReceiveFailure;
                            break;
                        case NSUrlErrorExtended.SecureConnectionFailed:
                            webExceptionStatus = WebExceptionStatus.SecureChannelFailure;
                            break;
                        case NSUrlErrorExtended.ServerCertificateHasBadDate:
                        case NSUrlErrorExtended.ServerCertificateHasUnknownRoot:
                        case NSUrlErrorExtended.ServerCertificateNotYetValid:
                        case NSUrlErrorExtended.ServerCertificateUntrusted:
                        case NSUrlErrorExtended.ClientCertificateRejected:
                        case NSUrlErrorExtended.ClientCertificateRequired:
                            webExceptionStatus = WebExceptionStatus.TrustFailure;
                            break;
                    }

                    goto done;
                }

                if (error.Domain == CFNetworkError.ErrorDomain) {
                    // Convert the error code into an enumeration (this is future
                    // proof, rather than just casting integer)
                    CFNetworkErrors networkError;
                    if (!Enum.TryParse<CFNetworkErrors>(error.Code.ToString(), out networkError)) {
                        networkError = CFNetworkErrors.CFHostErrorUnknown;
                    }

                    // Parse the enum into a web exception status or exception. Some
                    // of these values don't necessarily translate completely to
                    // what WebExceptionStatus supports, so made some best guesses
                    // here.  For your reading pleasure, compare these:
                    //
                    // Apple docs: https://developer.apple.com/library/ios/documentation/Networking/Reference/CFNetworkErrors/#//apple_ref/c/tdef/CFNetworkErrors
                    // .NET docs: http://msdn.microsoft.com/en-us/library/system.net.webexceptionstatus(v=vs.110).aspx
                    switch (networkError) {
                        case CFNetworkErrors.CFURLErrorCancelled:
                        case CFNetworkErrors.CFURLErrorUserCancelledAuthentication:
                        case CFNetworkErrors.CFNetServiceErrorCancel:
                            // No more processing is required so just return.
                            return new OperationCanceledException(error.LocalizedDescription, innerException);
                        case CFNetworkErrors.CFSOCKS5ErrorBadCredentials:
                        case CFNetworkErrors.CFSOCKS5ErrorUnsupportedNegotiationMethod:
                        case CFNetworkErrors.CFSOCKS5ErrorNoAcceptableMethod:
                        case CFNetworkErrors.CFErrorHttpAuthenticationTypeUnsupported:
                        case CFNetworkErrors.CFErrorHttpBadCredentials:
                        case CFNetworkErrors.CFErrorHttpBadURL:
                        case CFNetworkErrors.CFURLErrorBadURL:
                        case CFNetworkErrors.CFURLErrorUnsupportedURL:
                        case CFNetworkErrors.CFURLErrorCannotConnectToHost:
                        case CFNetworkErrors.CFURLErrorResourceUnavailable:
                        case CFNetworkErrors.CFURLErrorNotConnectedToInternet:
                        case CFNetworkErrors.CFURLErrorUserAuthenticationRequired:
                        case CFNetworkErrors.CFURLErrorInternationalRoamingOff:
                        case CFNetworkErrors.CFURLErrorCallIsActive:
                        case CFNetworkErrors.CFURLErrorDataNotAllowed:
                            webExceptionStatus = WebExceptionStatus.ConnectFailure;
                            break;
                        case CFNetworkErrors.CFURLErrorTimedOut:
                        case CFNetworkErrors.CFNetServiceErrorTimeout:
                            webExceptionStatus = WebExceptionStatus.Timeout;
                            break;
                        case CFNetworkErrors.CFHostErrorHostNotFound:
                        case CFNetworkErrors.CFURLErrorCannotFindHost:
                        case CFNetworkErrors.CFURLErrorDNSLookupFailed:
                        case CFNetworkErrors.CFNetServiceErrorDNSServiceFailure:
                            webExceptionStatus = WebExceptionStatus.NameResolutionFailure;
                            break;
                        case CFNetworkErrors.CFURLErrorDataLengthExceedsMaximum:
                            webExceptionStatus = WebExceptionStatus.MessageLengthLimitExceeded;
                            break;
                        case CFNetworkErrors.CFErrorHttpConnectionLost:
                        case CFNetworkErrors.CFURLErrorNetworkConnectionLost:
                            webExceptionStatus = WebExceptionStatus.ConnectionClosed;
                            break;
                        case CFNetworkErrors.CFErrorHttpRedirectionLoopDetected:
                        case CFNetworkErrors.CFURLErrorHTTPTooManyRedirects:
                        case CFNetworkErrors.CFURLErrorRedirectToNonExistentLocation:
                            webExceptionStatus = WebExceptionStatus.ProtocolError;
                            break;
                        case CFNetworkErrors.CFSOCKSErrorUnknownClientVersion:
                        case CFNetworkErrors.CFSOCKSErrorUnsupportedServerVersion:
                        case CFNetworkErrors.CFErrorHttpParseFailure:
                        case CFNetworkErrors.CFURLErrorRequestBodyStreamExhausted:
                            webExceptionStatus = WebExceptionStatus.SendFailure;
                            break;
                        case CFNetworkErrors.CFSOCKS4ErrorRequestFailed:
                        case CFNetworkErrors.CFSOCKS4ErrorIdentdFailed:
                        case CFNetworkErrors.CFSOCKS4ErrorIdConflict:
                        case CFNetworkErrors.CFSOCKS4ErrorUnknownStatusCode:
                        case CFNetworkErrors.CFSOCKS5ErrorBadState:
                        case CFNetworkErrors.CFSOCKS5ErrorBadResponseAddr:
                        case CFNetworkErrors.CFURLErrorBadServerResponse:
                        case CFNetworkErrors.CFURLErrorZeroByteResource:
                        case CFNetworkErrors.CFURLErrorCannotDecodeRawData:
                        case CFNetworkErrors.CFURLErrorCannotDecodeContentData:
                        case CFNetworkErrors.CFURLErrorCannotParseResponse:
                        case CFNetworkErrors.CFURLErrorFileDoesNotExist:
                        case CFNetworkErrors.CFURLErrorFileIsDirectory:
                        case CFNetworkErrors.CFURLErrorNoPermissionsToReadFile:
                        case CFNetworkErrors.CFURLErrorCannotLoadFromNetwork:
                        case CFNetworkErrors.CFURLErrorCannotCreateFile:
                        case CFNetworkErrors.CFURLErrorCannotOpenFile:
                        case CFNetworkErrors.CFURLErrorCannotCloseFile:
                        case CFNetworkErrors.CFURLErrorCannotWriteToFile:
                        case CFNetworkErrors.CFURLErrorCannotRemoveFile:
                        case CFNetworkErrors.CFURLErrorCannotMoveFile:
                        case CFNetworkErrors.CFURLErrorDownloadDecodingFailedMidStream:
                        case CFNetworkErrors.CFURLErrorDownloadDecodingFailedToComplete:
                        case CFNetworkErrors.CFHTTPCookieCannotParseCookieFile:
                        case CFNetworkErrors.CFNetServiceErrorUnknown:
                        case CFNetworkErrors.CFNetServiceErrorCollision:
                        case CFNetworkErrors.CFNetServiceErrorNotFound:
                        case CFNetworkErrors.CFNetServiceErrorInProgress:
                        case CFNetworkErrors.CFNetServiceErrorBadArgument:
                        case CFNetworkErrors.CFNetServiceErrorInvalid:
                            webExceptionStatus = WebExceptionStatus.ReceiveFailure;
                            break;
                        case CFNetworkErrors.CFURLErrorServerCertificateHasBadDate:
                        case CFNetworkErrors.CFURLErrorServerCertificateUntrusted:
                        case CFNetworkErrors.CFURLErrorServerCertificateHasUnknownRoot:
                        case CFNetworkErrors.CFURLErrorServerCertificateNotYetValid:
                        case CFNetworkErrors.CFURLErrorClientCertificateRejected:
                        case CFNetworkErrors.CFURLErrorClientCertificateRequired:
                            webExceptionStatus = WebExceptionStatus.TrustFailure;
                            break;
                        case CFNetworkErrors.CFURLErrorSecureConnectionFailed:
                            webExceptionStatus = WebExceptionStatus.SecureChannelFailure;
                            break;
                        case CFNetworkErrors.CFErrorHttpProxyConnectionFailure:
                        case CFNetworkErrors.CFErrorHttpBadProxyCredentials:
                        case CFNetworkErrors.CFErrorPACFileError:
                        case CFNetworkErrors.CFErrorPACFileAuth:
                        case CFNetworkErrors.CFErrorHttpsProxyConnectionFailure:
                        case CFNetworkErrors.CFStreamErrorHttpsProxyFailureUnexpectedResponseToConnectMethod:
                            webExceptionStatus = WebExceptionStatus.RequestProhibitedByProxy;
                            break;
                    }

                    goto done;
                }

            done:

                // Always create a WebException so that it can be handled by the client.
                ret = new WebException(error.LocalizedDescription, innerException, webExceptionStatus, response: null);
                return ret;
            }
        }
    }

    class ByteArrayListStream : Stream
    {
        Exception exception;
        IDisposable lockRelease;
        readonly AsyncLock readStreamLock;
        readonly List<byte[]> bytes = new List<byte[]>();

        bool isCompleted;
        long maxLength = 0;
        long position = 0;
        int offsetInCurrentBuffer = 0;

        public ByteArrayListStream()
        {
            // Initially we have nothing to read so Reads should be parked
            readStreamLock = AsyncLock.CreateLocked(out lockRelease);
        }

        public override bool CanRead { get { return true; } }
        public override bool CanWrite { get { return false; } }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        public override void WriteByte(byte value) { throw new NotSupportedException(); }
        public override bool CanSeek { get { return false; } }
        public override bool CanTimeout { get { return false; } }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override long Position {
            get { return position; }
            set {
                throw new NotSupportedException();
            }
        }

        public override long Length {
            get {
                return maxLength;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return this.ReadAsync(buffer, offset, count).Result;
        }

        /* OMG THIS CODE IS COMPLICATED
         *
         * Here's the core idea. We want to create a ReadAsync function that
         * reads from our list of byte arrays **until it gets to the end of
         * our current list**.
         *
         * If we're not there yet, we keep returning data, serializing access
         * to the underlying position pointer (i.e. we definitely don't want
         * people concurrently moving position along). If we try to read past
         * the end, we return the section of data we could read and complete
         * it.
         *
         * Here's where the tricky part comes in. If we're not Completed (i.e.
         * the caller still wants to add more byte arrays in the future) and
         * we're at the end of the current stream, we want to *block* the read
         * (not blocking, but async blocking whatever you know what I mean),
         * until somebody adds another byte[] to chew through, or if someone
         * rewinds the position.
         *
         * If we *are* completed, we should return zero to simply complete the
         * read, signalling we're at the end of the stream */
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
        retry:
            int bytesRead = 0;
            int buffersToRemove = 0;

            if (isCompleted && position == maxLength) {
                return 0;
            }

            if (exception != null) throw exception;

            using (await readStreamLock.LockAsync().ConfigureAwait(false)) {
                lock (bytes) {
                    foreach (var buf in bytes) {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (exception != null) throw exception;

                        int toCopy = Math.Min(count, buf.Length - offsetInCurrentBuffer);
                        Array.ConstrainedCopy(buf, offsetInCurrentBuffer, buffer, offset, toCopy);

                        count -= toCopy;
                        offset += toCopy;
                        bytesRead += toCopy;

                        offsetInCurrentBuffer += toCopy;

                        if (offsetInCurrentBuffer >= buf.Length) {
                            offsetInCurrentBuffer = 0;
                            buffersToRemove++;
                        }

                        if (count <= 0) break;
                    }

                    // Remove buffers that we read in this operation
                    bytes.RemoveRange(0, buffersToRemove);

                    position += bytesRead;
                }
            }

            // If we're at the end of the stream and it's not done, prepare
            // the next read to park itself unless AddByteArray or Complete 
            // posts
            if (position >= maxLength && !isCompleted) {
                lockRelease = await readStreamLock.LockAsync().ConfigureAwait(false);
            }

            if (bytesRead == 0 && !isCompleted) {
                // NB: There are certain race conditions where we somehow acquire
                // the lock yet are at the end of the stream, and we're not completed
                // yet. We should try again so that we can get stuck in the lock.
                goto retry;
            }

            if (cancellationToken.IsCancellationRequested) {
                Interlocked.Exchange(ref lockRelease, EmptyDisposable.Instance).Dispose();
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (exception != null) {
                Interlocked.Exchange(ref lockRelease, EmptyDisposable.Instance).Dispose();
                throw exception;
            }

            if (isCompleted && position < maxLength) {
                // NB: This solves a rare deadlock 
                //
                // 1. ReadAsync called (waiting for lock release)
                // 2. AddByteArray called (release lock)
                // 3. AddByteArray called (release lock)
                // 4. Complete called (release lock the last time)
                // 5. ReadAsync called (lock released at this point, the method completed successfully) 
                // 6. ReadAsync called (deadlock on LockAsync(), because the lock is block, and there is no way to release it)
                // 
                // Current condition forces the lock to be released in the end of 5th point

                Interlocked.Exchange(ref lockRelease, EmptyDisposable.Instance).Dispose();
            }

            return bytesRead;
        }

        public void AddByteArray(byte[] arrayToAdd)
        {
            if (exception != null) throw exception;
            if (isCompleted) throw new InvalidOperationException("Can't add byte arrays once Complete() is called");

            lock (bytes) {
                maxLength += arrayToAdd.Length;
                bytes.Add(arrayToAdd);
                //Console.WriteLine("Added a new byte array, {0}: max = {1}", arrayToAdd.Length, maxLength);
            }

            Interlocked.Exchange(ref lockRelease, EmptyDisposable.Instance).Dispose();
        }

        public void Complete()
        {
            isCompleted = true;
            Interlocked.Exchange(ref lockRelease, EmptyDisposable.Instance).Dispose();
        }

        public void SetException(Exception ex)
        {
            exception = ex;
            Complete();
        }
    }

    sealed class CancellableStreamContent : ProgressStreamContent
    {
        Action onDispose;

        public CancellableStreamContent(Stream source, Action onDispose) : base(source, CancellationToken.None)
        {
            this.onDispose = onDispose;
        }

        protected override void Dispose(bool disposing)
        {
            var disp = Interlocked.Exchange(ref onDispose, null);
            if (disp != null) disp();

            // EVIL HAX: We have to let at least one ReadAsync of the underlying
            // stream fail with OperationCancelledException before we can dispose
            // the base, or else the exception coming out of the ReadAsync will
            // be an ObjectDisposedException from an internal MemoryStream. This isn't
            // the Ideal way to fix this, but #yolo.
            Task.Run(() => base.Dispose(disposing));
        }
    }

    sealed class EmptyDisposable : IDisposable
    {
        static readonly IDisposable instance = new EmptyDisposable();
        public static IDisposable Instance { get { return instance; } }

        EmptyDisposable() { }
        public void Dispose() { }
    }
}
