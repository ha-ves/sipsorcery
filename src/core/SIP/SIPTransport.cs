﻿//-----------------------------------------------------------------------------
// Filename: SIPTransport.cs
//
// Description: SIP transport layer implementation. Handles different network
// transport options, retransmits, timeouts and transaction matching.
//
// Author(s):
// Aaron Clauson (aaron@sipsorcery.com)
// 
// History:
// 14 Feb 2006  Aaron Clauson   Created, Dublin, Ireland.
// 26 Apr 2008  Aaron Clauson   Added TCP support.
// 16 Oct 2019  Aaron Clauson   Added IPv6 support.
// 25 Oct 2019  Aaron Clauson   Added async options for sending requests and responses.
// 30 Oct 2019  Aaron Clauson   Added support for reliable provisional responses as per RFC3262.
//
// License: 
// BSD 3-Clause "New" or "Revised" License, see included LICENSE.md file.
//-----------------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SIPSorcery.Sys;

namespace SIPSorcery.SIP
{
    public delegate Task<SIPEndPoint> ResolveSIPUriDelegateAsync(SIPURI uri, bool preferIPv6, CancellationToken ct);
    public delegate SIPEndPoint ResolveSIPUriFromCacheDelegate(SIPURI uri, bool preferIPv6);

    public class SIPTransport : IDisposable
    {
        private const int MAX_QUEUEWAIT_PERIOD = 200;              // Maximum time to wait to check the message received queue if no events are received.
        private const string RECEIVE_THREAD_NAME = "siptrans-recv";

        private static string m_looseRouteParameter = SIPConstants.SIP_LOOSEROUTER_PARAMETER;
        public static IPAddress BlackholeAddress = IPAddress.Any;  // (IPAddress.Any is 0.0.0.0) Any SIP messages with this IP address will be dropped.

        /// <summary>
        /// Set to true to prefer IPv6 lookups of hostnames. By default IPv4 lookups will be performed.
        /// </summary>
        public bool PreferIPv6NameResolution = false;

        private static ILogger logger = Log.Logger;

        /// <summary>
        /// Determines whether the transport later will queue incoming requests for processing on a separate thread of process
        /// immediately on the same thread. Most SIP elements with the exception of Stateless Proxies will typically want to 
        /// queue incoming SIP messages.
        /// </summary>
        private bool m_queueIncoming = true;

        private bool m_transportThreadStarted = false;
        private ConcurrentQueue<IncomingMessage> m_inMessageQueue = new ConcurrentQueue<IncomingMessage>();
        private ManualResetEvent m_inMessageArrived = new ManualResetEvent(false);
        private CancellationTokenSource m_cts = new CancellationTokenSource();
        private bool m_closed = false;

        private readonly Encoding m_sipEncoding;
        private readonly Encoding m_sipBodyEncoding;

        /// <summary>
        /// If true allows this class to attempt to create a new SIP channel if a required protocol
        /// is missing. Set to false to prevent new channels being created on demand.
        /// Note that when listening SIP end points are required they will always need to be
        /// created manually.
        /// </summary>
        public bool CanCreateMissingChannels { get; set; } = true;

        /// <summary>
        /// The maximum number of SIP message receiving queues, if this number is exceeded, new messages will be discarded directly
        /// Default:5000,Unlimited:0
        /// </summary>
        public int MaxInMessageQueueCount { get; set; } = 5000;

        /// <summary>
        /// List of the SIP channels that have been opened and are under management by this instance.
        /// The dictionary key is channel ID (previously was a serialised SIP end point).
        /// </summary>
        private ConcurrentDictionary<string, SIPChannel> m_sipChannels = new ConcurrentDictionary<string, SIPChannel>();

        internal SIPTransactionEngine m_transactionEngine;

        /// <summary>
        /// Default call to do DNS lookups for SIP URI's. In normal circumstances this property does not need to
        /// be set manually and care needs to be taken if it is. Can be replaced for custom scenarios
        /// and unit testing.
        /// </summary>
        public ResolveSIPUriDelegateAsync ResolveSIPUriCallbackAsync;

        /// <summary>
        /// Default call to do DNS lookups for SIP URI's from cache and avoid a time consuming full DNS lookup. 
        /// In normal circumstances this property does not need to be set manually and care needs to be taken if 
        /// it is. Can be replaced for custom scenarios and unit testing.
        /// </summary>
        public ResolveSIPUriFromCacheDelegate ResolveSIPUriFromCacheCallback;

        public event SIPTransportRequestAsyncDelegate SIPTransportRequestReceived;
        public event SIPTransportResponseAsyncDelegate SIPTransportResponseReceived;
        public event STUNRequestReceivedDelegate STUNRequestReceived;

        public event SIPTransportRequestTraceDelegate SIPRequestInTraceEvent;
        public event SIPTransportRequestTraceDelegate SIPRequestOutTraceEvent;
        public event SIPTransportResponseTraceDelegate SIPResponseInTraceEvent;
        public event SIPTransportResponseTraceDelegate SIPResponseOutTraceEvent;
        public event SIPTransportSIPBadMessageDelegate SIPBadRequestInTraceEvent;
        public event SIPTransportSIPBadMessageDelegate SIPBadResponseInTraceEvent;
        public event SIPTransactionRequestRetransmitDelegate SIPRequestRetransmitTraceEvent;
        public event SIPTransactionResponseRetransmitDelegate SIPResponseRetransmitTraceEvent;

        /// <summary>
        /// If set this host name (or IP address) will be set whenever the transport layer is asked to
        /// do a substitution on the Contact URI. The substitution is requested by a request or response
        /// setting the Contact URI host to IPAddress.Any ("0.0.0.0") or IPAddress.IPV6.Any ("::0").
        /// <code>
        /// var sipRequest = GetRequest(
        ///   method,
        ///   uri,
        ///   new SIPToHeader(
        ///     null, 
        ///     new SIPURI(uri.User, uri.Host, null, uri.Scheme, SIPProtocolsEnum.udp), 
        ///     null),
        ///   SIPFromHeader.GetDefaultSIPFromHeader(uri.Scheme));
        ///   
        /// // Set the Contact header to a default value that lets the transport layer know to update it
        /// // when the sending socket is selected.
        /// sipRequest.Header.Contact = new List&lt;SIPContactHeader&gt;() { SIPContactHeader.GetDefaultSIPContactHeader() };
        /// </code>
        /// </summary>
        public string ContactHost;

        /// <summary>
        /// Optional callback function that can be set to customise the headers on an outbound SIP request.
        /// The callback is called BEFORE applying <seealso cref="ContactHost"/> which means do not set
        /// both if the callback is intended to set the Contact URI.
        /// Parameters:
        ///  - SIPEndPoint: The local SIP end point the request will be sent from.
        ///  - SIPEndPoint: The remote SIP end point the request has been resolved to.
        ///  - SIPRequest: The SIP request being sent.
        /// Returns: If the result is non-null it will be used to replace the current SIP Header
        /// instance on the SIP Request. If null the original header will be left in place.
        /// </summary>
        public Func<SIPEndPoint, SIPEndPoint, SIPRequest, SIPHeader> CustomiseRequestHeader;

        /// <summary>
        /// Optional function that can be set to customise the headers on an outbound SIP request.
        /// The callback is called BEFORE applying <seealso cref="ContactHost"/> which means do not set
        /// both if the callback is intended to set the Contact URI.
        /// Parameters:
        ///  - SIPEndPoint: The local SIP end point the request will be sent from.
        ///  - SIPEndPoint: The remote SIP end point the request has been resolved to.
        ///  - SIPRequest: The SIP request being sent.
        /// Returns: If the result is non-null it will be used to replace the current SIP Header
        /// instance on the SIP Request. If null the original header will be left in place.
        /// </summary>
        public Func<SIPEndPoint, SIPEndPoint, SIPResponse, SIPHeader> CustomiseResponseHeader;

        /// <summary>
        /// Warning: Do not set this property unless there is a specific problem with a remote
        /// SIP User Agent accepting SIP retransmits. The effect of setting this property is
        /// to only send each request and response for a transaction once, i.e. retransmits
        /// timers firing will not cause additional sending of the requests or responses to be
        /// put on the wire. SIP transaction processing will still occur as normal with the 
        /// exception of not sending the retransmitted messages. It's also only likely to
        /// be useful for cases where reliable transports, such as TCP and TLS, are being used,
        /// since they are the ones where retransmits have been observed to be misidentified.
        /// </summary>
        /// <remarks>
        /// For additional context see https://lists.cs.columbia.edu/pipermail/sip-implementors/2013-January/028817.html
        /// and https://github.com/sipsorcery/sipsorcery/issues/370#issuecomment-739495726.
        /// </remarks>
        public bool DisableRetransmitSending
        {
            get
            {
                if (m_transactionEngine != null)
                {
                    return m_transactionEngine.DisableRetransmitSending;
                }
                else
                {
                    return false;
                }
            }
            set
            {
                if (m_transactionEngine != null)
                {
                    m_transactionEngine.DisableRetransmitSending = value;
                }
            }
        }

        /// <summary>
        /// Warning: Do not set this property unless you explicitly require a very high number of 
        /// in-flight SIP transactions. The default limit is high and increasing it is likely to
        /// have a significant impact on CPU and memory performance.
        /// </summary>
        public static int MaxPendingTransactionsCount
        {
            get => SIPTransactionEngine.MaxReliableTranismissionsCount;
            set => SIPTransactionEngine.MaxReliableTranismissionsCount = value;
        }

        /// <summary>
        /// Creates a SIP transport class with default DNS resolver and SIP transaction engine.
        /// </summary>
        public SIPTransport() : this(false)
        {
        }

        public SIPTransport(Encoding sipEncoding, Encoding sipBodyEncoding) : this(false, sipEncoding, sipBodyEncoding)
        {
        }

        public SIPTransport(bool stateless) : this(stateless, SIPConstants.DEFAULT_ENCODING, SIPConstants.DEFAULT_ENCODING)
        {
        }

        /// <summary>
        /// Allows the transport layer to be created to operate in a stateless mode.
        /// </summary>
        /// <param name="stateless">If true the transport layer will NOT queue incoming messages
        /// and will NOT use a transaction engine.</param>
        /// <param name="sipEncoding"></param>
        /// <param name="sipBodyEncoding"></param>
        public SIPTransport(bool stateless, Encoding sipEncoding, Encoding sipBodyEncoding)
        {
            m_sipEncoding = sipEncoding;
            m_sipBodyEncoding = sipBodyEncoding;
            ResolveSIPUriCallbackAsync = SIPDns.ResolveAsync;
            ResolveSIPUriFromCacheCallback = SIPDns.ResolveFromCache;

            if (stateless)
            {
                m_queueIncoming = false;
            }
            else
            {
                m_queueIncoming = true;
                //ResolveSIPEndPoint_External = SIPDNSManager.ResolveSIPService;
                m_transactionEngine = new SIPTransactionEngine(this);
                m_transactionEngine.SIPRequestRetransmitTraceEvent += (tx, req, count) => SIPRequestRetransmitTraceEvent?.Invoke(tx, req, count);
                m_transactionEngine.SIPResponseRetransmitTraceEvent += (tx, resp, count) => SIPResponseRetransmitTraceEvent?.Invoke(tx, resp, count);
            }
        }

        /// <summary>
        /// Adds additional SIP Channels to the transport layer.
        /// </summary>
        public void AddSIPChannel(List<SIPChannel> sipChannels)
        {
            foreach (SIPChannel sipChannel in sipChannels)
            {
                AddSIPChannel(sipChannel);
            }
        }

        /// <summary>
        /// Adds an additional SIP Channel to the transport layer.
        /// </summary>
        public void AddSIPChannel(SIPChannel sipChannel)
        {
            try
            {
                if (m_sipChannels.TryAdd(sipChannel.ID, sipChannel))
                {
                    // Wire up the SIP transport to the SIP channel.
                    sipChannel.SIPMessageReceived += ReceiveMessage;

                    if (m_queueIncoming && !m_transportThreadStarted)
                    {
                        // Starts tasks to process queued SIP messages.
                        m_transportThreadStarted = true;
                        Task.Factory.StartNew(ProcessReceiveQueue, TaskCreationOptions.LongRunning);
                    }
                }
                else
                {
                    throw new ApplicationException("Failed to add SIPChannel to the SIP transport.");
                }
            }
            catch (Exception excp)
            {
                logger.LogError(excp, "Exception AddSIPChannel. {ErrorMessage}", excp.Message);
                throw;
            }
        }

        /// <summary>
        /// Removes a single SIP channel from the transport layer.
        /// </summary>
        /// <param name="sipChannel">The SIP channel to remove.</param>
        public void RemoveSIPChannel(SIPChannel sipChannel)
        {
            if (m_sipChannels.ContainsKey(sipChannel.ID))
            {
                m_sipChannels.TryRemove(sipChannel.ID, out _);
                sipChannel.SIPMessageReceived -= ReceiveMessage;
            }
        }

        /// <summary>
        /// Shuts down the SIP transport layer by closing all SIP channels and stopping long running tasks.
        /// </summary>
        public void Shutdown()
        {
            try
            {
                m_closed = true;
                m_cts.Cancel();
                m_inMessageArrived.Set();
                m_transactionEngine?.Shutdown();
                m_transactionEngine?.Dispose();

                foreach (SIPChannel channel in m_sipChannels.Values)
                {
                    channel.Close();
                }
            }
            catch (Exception excp)
            {
                logger.LogError(excp, "Exception SIPTransport Shutdown. {ErrorMessage}", excp.Message);
            }
        }

        /// <summary>
        /// Event handler for messages received on all SIP channels assigned to this transport. There 
        /// are two distinct modes of operation for processing messages depending on whether the queue
        /// incoming variable is set. If it is then new messages get added to a queue and are processed on
        /// a separate thread. If not then the message is processed on the same thread that received the 
        /// message. Generally only applications that do minimal processing, such as a stateless SIP Proxy,
        /// should do without the queueing. The biggest blocking risk is DNS. If the message is processed
        /// on the SIP channel thread and results in a DNS lookup then new receives could be blocked for 
        /// up to 10s.
        /// </summary>
        /// <param name="sipChannel">The SIP channel that received the message.</param>
        /// <param name="localEndPoint">The local end point the message was received on.</param>
        /// <param name="remoteEndPoint">The remote end point the message came from.</param>
        /// <param name="buffer">A buffer containing the received message.</param>
        public Task ReceiveMessage(SIPChannel sipChannel, SIPEndPoint localEndPoint, SIPEndPoint remoteEndPoint, byte[] buffer)
        {
            try
            {
                if (!m_queueIncoming)
                {
                    return SIPMessageReceived(sipChannel, localEndPoint, remoteEndPoint, buffer);
                }
                else
                {
                    IncomingMessage incomingMessage = new IncomingMessage(sipChannel, localEndPoint, remoteEndPoint, buffer);

                    // Keep the queue within size limits 
                    if (MaxInMessageQueueCount > 0 && m_inMessageQueue.Count >= MaxInMessageQueueCount)
                    {
                        logger.LogWarning("SIPTransport queue full new message from {RemoteEndPoint} being discarded.", remoteEndPoint);
                    }
                    else
                    {
                        m_inMessageQueue.Enqueue(incomingMessage);
                    }

                    m_inMessageArrived.Set();

                    return Task.CompletedTask;
                }
            }
            catch (Exception excp)
            {
                logger.LogError(excp, "Exception SIPTransport ReceiveMessage. {ErrorMessage}", excp.Message);
                throw;
            }
        }

        /// <summary>
        /// This function performs processing on a request to handle any actions that need to be taken based on the Route header.
        /// </summary>
        /// <remarks>
        /// The main sections in the RFC3261 dealing with Route header processing are sections 12.2.1.1 for request processing and
        /// 16.4 for proxy processing.
        /// The steps to process requests for Route headers are:
        ///  1. If route set is empty no further action is required, forward to destination resolved from request URI,
        ///  2. If the request URI is identified as a value that was previously set as a Route by this SIP agent it means the
        ///     previous hop was a strict router. Replace the request URI with the last Route header and go to next step,
        ///  3. If the top most route header was set by this SIP agent then remove it and go to next step,
        ///  4. If the top most route set does contain the lr parameter then forward to the destination resolved by it,
        ///  5. If the top most route header does NOT contain the lr parameter is must be popped and inserted as the request URI
        ///     and the original request URI must be added to the end of the route set, forward to destination resolved from request URI,
        /// </remarks>
        public void PreProcessRouteInfo(SIPRequest sipRequest)
        {
            // If there are no routes defined then there is nothing to do.
            if (sipRequest.Header.Routes != null && sipRequest.Header.Routes.Length > 0)
            {
                // If this stack's route URI is being used as the request URI then it will have the loose route parameter (see remarks step 2).
                if (sipRequest.URI.Parameters.Has(m_looseRouteParameter))
                {
                    foreach (SIPChannel sipChannel in m_sipChannels.Values)
                    {
                        if (sipChannel.IsChannelSocket(sipRequest.URI.Host))
                        {
                            // The request URI was this router's address so it was set by a strict router.
                            // Replace the URI with the original SIP URI that is stored at the end of the route header.
                            sipRequest.URI = sipRequest.Header.Routes.BottomRoute.URI;
                            sipRequest.Header.Routes.RemoveBottomRoute();
                        }
                    }
                }

                // The possibility of a strict router on the previous hop has now been handled. 
                if (sipRequest.Header.Routes != null && sipRequest.Header.Routes.Length > 0)
                {
                    // Check whether the top route header belongs to this proxy (see remarks step 3).
                    if (!sipRequest.Header.Routes.TopRoute.IsStrictRouter)
                    {
                        foreach (SIPChannel sipChannel in m_sipChannels.Values)
                        {
                            // TODO: For IPAddress.Any have to check all available IP addresses not just listening one.
                            if (sipChannel.IsChannelSocket(sipRequest.Header.Routes.TopRoute.URI.Host))
                            {
                                // Remove the top route as it belongs to this proxy.
                                sipRequest.ReceivedRoute = sipRequest.Header.Routes.PopRoute();
                                break;
                            }
                        }
                    }

                    // Check whether the top route header is a strict router and if so adjust the request accordingly (see remarks step 5).
                    if (sipRequest.Header.Routes != null && sipRequest.Header.Routes.Length > 0)
                    {
                        if (sipRequest.Header.Routes.TopRoute.IsStrictRouter)
                        {
                            // Put the strict router's uri into the request URI and place the original request URI at the end of the route set.
                            SIPRoute strictRoute = sipRequest.Header.Routes.PopRoute();
                            SIPRoute uriRoute = new SIPRoute(sipRequest.URI);
                            sipRequest.Header.Routes.AddBottomRoute(uriRoute);
                            sipRequest.URI = strictRoute.URI;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Allows raw bytes to be sent from one of the SIPTransport sockets. This should not be used for SIP payloads and instead is
        /// provided to allow other types of payloads to be multiplexed on the SIP socket. Examples are sending NAT keep-alives and
        /// STUN responses where it's useful to use the same socket as the SIP packets.
        /// </summary>
        /// <param name="localSIPEndPoint">The local SIP end point to do the send from. Must match the local end point of one of
        /// the SIP transports channels.</param>
        /// <param name="dstEndPoint">The destination end point to send the buffer to.</param>
        /// <param name="buffer">The data buffer to send.</param>
        public Task<SocketError> SendRawAsync(SIPEndPoint localSIPEndPoint, SIPEndPoint dstEndPoint, byte[] buffer)
        {
            if (localSIPEndPoint == null)
            {
                throw new ArgumentNullException("localSIPEndPoint", "The local SIP end point must be set for SendRaw.");
            }
            else if (dstEndPoint == null)
            {
                throw new ArgumentNullException("dstEndPoint", "The destination end point must be set for SendRaw.");
            }
            if (dstEndPoint.Address.Equals(BlackholeAddress))
            {
                // Ignore packet, it's destined for the blackhole.
                return Task.FromResult(SocketError.Success);
            }

            SIPChannel sipChannel = m_sipChannels[localSIPEndPoint.ChannelID];
            return sipChannel.SendAsync(dstEndPoint, buffer, false, localSIPEndPoint.ConnectionID);
        }

        /// <summary>
        /// This send method does NOT wait if a DNS lookup is required. Instead it relies on the
        /// SIP retransmit logic to avoid to re-attempt the send at pre-defined intervals.
        /// This type of send is suitable for requests that are part of a transaction or for 
        /// SIP Proxy servers that are relying on the remote SIP agent to retransmit requests.
        /// </summary>
        /// <param name="sipRequest">The SIP request to send.</param>
        /// <param name="waitForDns">If true the request will wait for any required DNS lookup to 
        /// complete. This can potentially take many seconds. If false the DNS lookup will be
        /// queued and the send will need to be called again.</param>
        /// <returns>Will return InPorgress for a DNS cache miss. HostNotFound for a cache hit on a 
        /// failure response. Otherwise the result of the send attempt.</returns>
        public async Task<SocketError> SendRequestAsync(SIPRequest sipRequest, bool waitForDns = false)
        {
            if (sipRequest == null)
            {
                throw new ArgumentNullException(nameof(sipRequest), "The SIP request must be set for SendRequest.");
            }

            // The lookup logic is designed to take advantage of the SIP retransmit mechanism. Rather
            // than initiate the lookup and then wait for it to complete, which could take up to 20s
            // in extreme cases, the lookup is put on it's own thread and then when ready the result
            // will be used on the next SIP retransmit.

            SIPURI lookupURI = (sipRequest.Header.Routes != null && sipRequest.Header.Routes.Length > 0) ?
                sipRequest.Header.Routes.TopRoute.URI : sipRequest.URI;

            var cacheResult = ResolveSIPUriFromCacheCallback(lookupURI, PreferIPv6NameResolution);

            if (cacheResult != null && cacheResult != SIPEndPoint.Empty)
            {
                return await SendRequestAsync(cacheResult, sipRequest).ConfigureAwait(false);
            }
            else
            {
                if (waitForDns || DisableRetransmitSending)
                {
                    // This is the UNHAPPY path.
                    // If there was no cached DNS result then wait for a new resolution attempt to complete.
                    // DNS lookups can take a relatively LONG time, possibly >=20s with a poor DNS server.
                    // In ideal circumstances DON'T wait for DNS and instead use the SIP retransmit mechanism
                    // with its regular retry attempts to wait for DNS resolution.
                    cacheResult = ResolveSIPUriFromCacheCallback(lookupURI, PreferIPv6NameResolution);

                    if (cacheResult == null)
                    {
                        cacheResult = await ResolveSIPUriCallbackAsync(lookupURI, PreferIPv6NameResolution, m_cts.Token).ConfigureAwait(false);
                    }

                    if (cacheResult != null && cacheResult != SIPEndPoint.Empty)
                    {
                        return await SendRequestAsync(cacheResult, sipRequest).ConfigureAwait(false);
                    }
                    else
                    {
                        return SocketError.HostNotFound;
                    }
                }

                else
                {
                    // This is the HAPPY path.
                    // No existing success or failure entry in the cache. Initiate a lookup but DON'T wait for it.
                    _ = Task.Run(() => ResolveSIPUriCallbackAsync(lookupURI, PreferIPv6NameResolution, m_cts.Token).ConfigureAwait(false));
                    return SocketError.InProgress;
                }
            }
        }

        /// <summary>
        /// Sends a SIP request asynchronously. This method will attempt to find the most appropriate
        /// local SIP channel in this SIP transport to send the request on.
        /// </summary>
        /// <param name="dstEndPoint">The destination end point to send the request to.</param>
        /// <param name="sipRequest">The SIP request to send.</param>
        public Task<SocketError> SendRequestAsync(SIPEndPoint dstEndPoint, SIPRequest sipRequest)
        {
            if (dstEndPoint == null)
            {
                throw new ArgumentNullException(nameof(dstEndPoint), "The destination end point must be set for SendRequest.");
            }
            else if (sipRequest == null)
            {
                throw new ArgumentNullException(nameof(sipRequest), "The SIP request must be set for SendRequest.");
            }
            else if (dstEndPoint.Address.Equals(BlackholeAddress))
            {
                // Ignore packet, it's destined for the black-hole.
                return Task.FromResult(SocketError.Success);
            }

            SIPChannel sipChannel = GetSIPChannelForDestination(dstEndPoint.Protocol, dstEndPoint.GetIPEndPoint(), sipRequest.SendFromHintChannelID, false);
            SIPEndPoint sendFromSIPEndPoint = sipChannel.GetLocalSIPEndPointForDestination(dstEndPoint);

            // Optional callback to allow the application to customise the outgoing SIP Request's headers.
            if (CustomiseRequestHeader != null)
            {
                sipRequest.Header = CustomiseRequestHeader(sendFromSIPEndPoint, dstEndPoint, sipRequest) ?? sipRequest.Header;
            }

            // Once the channel has been determined check some specific header fields and replace the place holder end point.
            sipRequest.Header = AdjustHeadersForEndPoint(sendFromSIPEndPoint, sipRequest.Header);

            return SendRequestAsync(sipChannel, sendFromSIPEndPoint, dstEndPoint, sipRequest);
        }

        /// <summary>
        /// Attempts to send a SIP request to the destination end point using the specified SIP channel.
        /// </summary>
        /// <param name="sipChannel">The SIP channel to use to send the SIP request.</param>
        /// <param name="dstEndPoint">The destination to send the SIP request to.</param>
        /// <param name="sipRequest">The SIP request to send.</param>
        private Task<SocketError> SendRequestAsync(SIPChannel sipChannel, SIPEndPoint sendFromSIPEndPoint, SIPEndPoint dstEndPoint, SIPRequest sipRequest)
        {
            if (sipChannel == null)
            {
                throw new ArgumentNullException(nameof(sipChannel), "The SIP channel must be set for SendRequest.");
            }
            else if (dstEndPoint == null)
            {
                throw new ArgumentNullException(nameof(dstEndPoint), "The destination end point must be set for SendRequest.");
            }
            else if (sipRequest == null)
            {
                throw new ArgumentNullException(nameof(sipRequest), "The SIP request must be set for SendRequest.");
            }
            else if (dstEndPoint.Address.Equals(BlackholeAddress))
            {
                // Ignore packet, it's destined for the blackhole.
                return Task.FromResult(SocketError.Success);
            }

            sipRequest.Header.ContentLength = (sipRequest.BodyBuffer != null) ? sipRequest.BodyBuffer.Length : 0;

            SIPRequestOutTraceEvent?.Invoke(sendFromSIPEndPoint, dstEndPoint, sipRequest);

            if (sipChannel.IsSecure)
            {
                return sipChannel.SendSecureAsync(dstEndPoint, sipRequest.GetBytes(), sipRequest.URI.HostAddress, true, sipRequest.SendFromHintConnectionID);
            }
            else
            {
                return sipChannel.SendAsync(dstEndPoint, sipRequest.GetBytes(), true, sipRequest.SendFromHintConnectionID);
            }
        }

        /// <summary>
        /// Add a SIP transaction to the engine which then keeps track of whether a response/acknowledgement has been received.
        /// For UDP "reliably" means retransmitting the message up to eleven times.
        /// If no response is received then periodic retransmits are made for up to T1 x 64 seconds (defaults to 30 seconds with 11 retransmits).
        /// </summary>
        /// <param name="sipTransaction">The SIP transaction encapsulating the SIP request or response that needs to be sent reliably.</param>
        internal void AddTransaction(SIPTransaction sipTransaction)
        {
            if (sipTransaction == null)
            {
                throw new ArgumentNullException(nameof(sipTransaction), "The SIP transaction parameter must be set for AddTransaction.");
            }

            if (m_transactionEngine == null)
            {
                logger.LogWarning("SIP transport was requested to send a transaction in stateless mode (noop).");
            }
            else if (!m_transactionEngine.Exists(sipTransaction.TransactionId))
            {
                m_transactionEngine.AddTransaction(sipTransaction);
            }
        }

        /// <summary>
        /// Forwards a SIP response. There are two main cases for a SIP response to be forwarded:
        /// - First case is when we have processed a request and are returning a response. In this case the response
        ///   should be sent back on exactly the same socket the request came on.
        /// - Second case is when we are acting as a Proxy and the response is on it's way back from the agent
        ///   that processed the request. In this case it's highly likely the response needs to be forwarded to
        ///   a different end point then the one it came from and it's also possible it will need to use a completely
        ///   different channel to send on compared to the one it arrived on.
        /// 
        /// Forwarding logic:
        /// - If the channel hints are set then an attempt will be made to use them to find an appropriate channel to
        ///   send the response on. If the hinted channel can't be found or it is found but is the wrong protocol then
        ///   move onto the next step,
        /// - The information in the Top Via header will be used to find the best channel to forward the response on.
        /// This is a special send method that relies on the SIP transaction retransmit logic to avoid
        /// blocking when a DNS request is required. This type of send is suitable for responses that 
        /// are part of a transaction or for SIP Proxy servers that are relying on the remote 
        /// SIP agent to retransmit requests.
        /// </summary>
        /// <param name="sipResponse">The SIP response to send.</param>
        /// <returns>Will return InPorgress for a DNS cache miss. HostNotFound for a cache hit on a 
        /// failure response. Otherwise the result of the send attempt.</returns>
        /// <param name="waitForDns">If true the request will wait for any required DNS lookup to 
        /// complete. This can potentially take many seconds. If false the DNS lookup will be
        /// queued and the send will need to be called again.</param>
        public async Task<SocketError> SendResponseAsync(SIPResponse sipResponse, bool waitForDns = false)
        {
            if (sipResponse == null)
            {
                throw new ArgumentNullException(nameof(sipResponse), "The SIP response must be set for SendResponse.");
            }
            else if (sipResponse.Header.Vias?.TopViaHeader == null)
            {
                logger.LogWarning("There was no top Via header on a SIP response from {RemoteSIPEndPoint} in SendResponseAsync, response dropped.", sipResponse.RemoteSIPEndPoint);
                return SocketError.Fault;
            }
            else
            {
                var topViaHeader = sipResponse.Header.Vias.TopViaHeader;
                SIPURI topViaUri = new SIPURI(null, topViaHeader.ReceivedFromAddress, null, SIPSchemesEnum.sip, topViaHeader.Transport);

                var cacheResult = ResolveSIPUriFromCacheCallback(topViaUri, PreferIPv6NameResolution);

                if (cacheResult != null && cacheResult != SIPEndPoint.Empty)
                {
                    return await SendResponseAsync(cacheResult, sipResponse).ConfigureAwait(false);

                }
                else
                {
                    if (waitForDns || DisableRetransmitSending)
                    {
                        // UNHAPPY PATH.
                        // The send will block waiting for a DNS resolution.
                        cacheResult = await ResolveSIPUriCallbackAsync(topViaUri, PreferIPv6NameResolution, m_cts.Token).ConfigureAwait(false);

                        if (cacheResult != null && cacheResult != SIPEndPoint.Empty)
                        {
                            return await SendResponseAsync(cacheResult, sipResponse).ConfigureAwait(false);
                        }
                        else
                        {
                            return SocketError.HostNotFound;
                        }
                    }
                    else
                    {
                        // HAPPY PATH.
                        // The lookup logic is designed to take advantage of the SIP retransmit mechanism. Rather
                        // than initiate the lookup and then wait for it to complete, which could take up to 20s
                        // in extreme cases, the lookup is put on it's own thread and then when ready the result
                        // will be used on the next SIP retransmit.
                        // No existing success or failure entry in the cache. Initiate a lookup but DON'T wait for it.
                        _ = Task.Run(() => ResolveSIPUriCallbackAsync(topViaUri, PreferIPv6NameResolution, m_cts.Token).ConfigureAwait(false));
                        return SocketError.InProgress;
                    }
                }
            }
        }

        /// <summary>
        /// Asynchronously forwards a SIP response to the specified destination.
        /// </summary>
        /// <param name="dstEndPoint">The destination end point to send the response to.</param>
        /// <param name="sipResponse">The SIP response to send.</param>
        public Task<SocketError> SendResponseAsync(SIPEndPoint dstEndPoint, SIPResponse sipResponse)
        {
            if (dstEndPoint == null)
            {
                throw new ArgumentNullException(nameof(dstEndPoint), "The destination end point must be set for SendResponseAsync.");
            }
            else if (sipResponse == null)
            {
                throw new ArgumentNullException(nameof(sipResponse), "The SIP response must be set for SendResponseAsync.");
            }

            if (dstEndPoint.Address.Equals(BlackholeAddress))
            {
                // Ignore packet, it's destined for the black-hole.
                return Task.FromResult(SocketError.Success);
            }
            else
            {
                // Once the destination is known determine the local SIP channel to reach it.
                SIPChannel sendFromChannel = GetSIPChannelForDestination(dstEndPoint.Protocol, dstEndPoint.GetIPEndPoint(), sipResponse.SendFromHintChannelID, true);

                if (sendFromChannel == null)
                {
                    logger.LogWarning("An existing SIP channel could not be found to send response {ShortDescription}.", sipResponse.ShortDescription);
                    return Task.FromResult(SocketError.NotConnected);
                }
                else
                {
                    SIPEndPoint sendFromSIPEndPoint = sendFromChannel.GetLocalSIPEndPointForDestination(dstEndPoint);

                    // Optional callback to allow the application to customise the outgoing SIP Response's headers.
                    if (CustomiseResponseHeader != null)
                    {
                        sipResponse.Header = CustomiseResponseHeader(sendFromSIPEndPoint, dstEndPoint, sipResponse) ?? sipResponse.Header;
                    }

                    // Once the channel has been determined check some specific header fields and replace the place holder end point.
                    sipResponse.Header = AdjustHeadersForEndPoint(sendFromSIPEndPoint, sipResponse.Header);

                    sipResponse.Header.ContentLength = (sipResponse.BodyBuffer != null) ? sipResponse.BodyBuffer.Length : 0;

                    SIPResponseOutTraceEvent?.Invoke(sendFromSIPEndPoint, dstEndPoint, sipResponse);

                    // Now have a destination and sending channel, go ahead and forward.
                    return sendFromChannel.SendAsync(dstEndPoint, sipResponse.GetBytes(), false, sipResponse.SendFromHintConnectionID);
                }
            }
        }

        /// <summary>
        /// Checks specific SIP headers for "0.0.0.0" or "::0" strings and where found replaces them with the socket that the
        /// request or response is being sent from. This mechanism is used to allow higher level agents to indicate they want to defer
        /// the setting of those header fields to the transport class.
        /// </summary>
        /// <param name="sendFromSIPEndPoint">The IP end point the request or response is being sent from.</param>
        /// <param name="header">The SIP header object to apply the adjustments to. The header object will be updated
        /// in place with any header adjustments.</param>
        private SIPHeader AdjustHeadersForEndPoint(SIPEndPoint sendFromSIPEndPoint, SIPHeader header)
        {
            IPEndPoint sendFromEndPoint = sendFromSIPEndPoint.GetIPEndPoint();

            SIPHeader copy = null;

            // Top Via header.
            if (header.Vias.TopViaHeader.ContactAddress.StartsWith(IPAddress.Any.ToString()) ||
                header.Vias.TopViaHeader.ContactAddress.StartsWith(IPAddress.IPv6Any.ToString()))
            {
                copy = copy ?? header.Copy();
                copy.Vias.Via[0].Host = sendFromEndPoint.Address.ToString();
                copy.Vias.Via[0].Port = sendFromEndPoint.Port;
            }

            if (header.Vias.TopViaHeader.Transport != sendFromSIPEndPoint.Protocol)
            {
                copy = copy ?? header.Copy();
                copy.Vias.Via[0].Transport = sendFromSIPEndPoint.Protocol;
            }

            // From header.
            if (header.From.FromURI.Host.StartsWith(IPAddress.Any.ToString()) ||
                header.From.FromURI.Host.StartsWith(IPAddress.IPv6Any.ToString()))
            {
                copy = copy ?? header.Copy();
                copy.From.FromURI.Host = sendFromEndPoint.ToString();
            }

            // Contact header.
            if (header.Contact != null && header.Contact.Count == 1)
            {
                if (!string.IsNullOrEmpty(ContactHost))
                {
                    // A custom ContactHost will always take precedence.
                    copy = copy ?? header.Copy();
                    if (IPAddress.TryParse(ContactHost, out _))
                    {
                        // If the custom host is an IP address include the port number that's being used for the send.
                        copy.Contact.Single().ContactURI.Host = ContactHost + ":" + sendFromEndPoint.Port.ToString();
                    }
                    else
                    {
                        copy.Contact.Single().ContactURI.Host = ContactHost;
                    }
                }
                else if (header.Contact.Single().ContactURI.Host.StartsWith(IPAddress.Any.ToString()) ||
                    header.Contact.Single().ContactURI.Host.StartsWith(IPAddress.IPv6Any.ToString()))
                {
                    copy = copy ?? header.Copy();
                    copy.Contact.Single().ContactURI.Host = sendFromEndPoint.ToString();
                }

                if (header.Contact.Single().ContactURI.Scheme == SIPSchemesEnum.sip && sendFromSIPEndPoint.Protocol != SIPProtocolsEnum.udp)
                {
                    copy = copy ?? header.Copy();
                    copy.Contact.Single().ContactURI.Protocol = sendFromSIPEndPoint.Protocol;
                }
            }

            return copy ?? header;
        }

        /// <summary>
        /// Dedicated loop to process queued received messages.
        /// </summary>
        private void ProcessReceiveQueue()
        {
            Thread.CurrentThread.Name = RECEIVE_THREAD_NAME;

            try
            {
                while (!m_closed)
                {
                    while (m_inMessageQueue.Count > 0)
                    {
                        m_inMessageQueue.TryDequeue(out var incomingMessage);
                        if (incomingMessage != null)
                        {
                            SIPMessageReceived(incomingMessage.LocalSIPChannel, incomingMessage.LocalEndPoint, incomingMessage.RemoteEndPoint, incomingMessage.Buffer).Wait();
                        }
                    }

                    if (!m_closed)
                    {
                        m_inMessageArrived.Reset();
                        m_inMessageArrived.WaitOne(MAX_QUEUEWAIT_PERIOD);
                    }
                }
            }
            catch (Exception excp)
            {
                logger.LogError(excp, "Exception SIPTransport ProcessReceiveQueue. {ErrorMessage}", excp.Message);
            }
            finally
            {
                m_transportThreadStarted = false;
            }
        }

        /// <summary>
        /// Processes an incoming message from a SIP channel.
        /// </summary>
        /// <param name="sipChannel">The SIP channel the message was received on.</param>
        /// <param name="localEndPoint">The local end point that the SIP channel received the message on.</param>
        /// <param name="remoteEndPoint">The remote end point the message came from.</param>
        /// <param name="buffer">The raw message received.</param>
        private Task<SocketError> SIPMessageReceived(
            SIPChannel sipChannel,
            SIPEndPoint localEndPoint,
            SIPEndPoint remoteEndPoint,
            byte[] buffer)
        {
            string rawSIPMessage = null;

            try
            {
                if (buffer != null && buffer.Length > 0)
                {
                    if ((buffer[0] == 0x0 || buffer[0] == 0x1) && buffer.Length >= 20)
                    {
                        // Treat any messages that cannot be SIP as STUN requests.
                        STUNRequestReceived?.Invoke(localEndPoint.GetIPEndPoint(), remoteEndPoint.GetIPEndPoint(), buffer, buffer.Length);
                    }
                    else
                    {
                        // Treat all messages that don't match STUN requests as SIP.
                        if (buffer.Length > SIPConstants.SIP_MAXIMUM_RECEIVE_LENGTH)
                        {
                            string rawErrorMessage = m_sipEncoding.GetString(buffer, 0, 1024) + "\r\n..truncated";
                            SIPBadRequestInTraceEvent?.Invoke(localEndPoint, remoteEndPoint, "SIP message too large, " + buffer.Length + " bytes, maximum allowed is " + SIPConstants.SIP_MAXIMUM_RECEIVE_LENGTH + " bytes.", SIPValidationFieldsEnum.Request, rawErrorMessage);
                            SIPResponse tooLargeResponse = SIPResponse.GetResponse(localEndPoint, remoteEndPoint, SIPResponseStatusCodesEnum.MessageTooLarge, null);
                            return SendResponseAsync(tooLargeResponse);
                        }
                        else
                        {
                            // TODO: Future improvement (4.5.2 doesn't support) is to use a ReadOnlySpan to check for the existence 
                            // of 'S', 'I', 'P' before the first EOL.
                            rawSIPMessage = m_sipEncoding.GetString(buffer, 0, buffer.Length);
                            if (rawSIPMessage.IsNullOrBlank() || SIPMessageBuffer.IsPing(buffer))
                            {
                                // An empty transmission has been received. More than likely this is a NAT keep alive and can be disregarded.
                                return Task.FromResult(SocketError.Success);
                            }
                            else if (!rawSIPMessage.Contains("SIP"))
                            {
                                SIPBadRequestInTraceEvent?.Invoke(localEndPoint, remoteEndPoint, "Missing SIP string.", SIPValidationFieldsEnum.NoSIPString, rawSIPMessage);
                                return Task.FromResult(SocketError.InvalidArgument);
                            }

                            var sipMessageBuffer = SIPMessageBuffer.ParseSIPMessage(buffer, m_sipEncoding, m_sipBodyEncoding, localEndPoint, remoteEndPoint);

                            if (sipMessageBuffer != null)
                            {
                                if (sipMessageBuffer.SIPMessageType == SIPMessageTypesEnum.Response)
                                {
                                    #region SIP Response.

                                    try
                                    {
                                        SIPResponse sipResponse = SIPResponse.ParseSIPResponse(sipMessageBuffer, m_sipEncoding, m_sipBodyEncoding);

                                        SIPResponseInTraceEvent?.Invoke(localEndPoint, remoteEndPoint, sipResponse);

                                        if (m_transactionEngine != null && m_transactionEngine.Exists(sipResponse))
                                        {
                                            SIPTransaction transaction = m_transactionEngine.GetTransaction(sipResponse);
                                            transaction.GotResponse(localEndPoint, remoteEndPoint, sipResponse);
                                        }
                                        else
                                        {
                                            SIPTransportResponseReceived?.Invoke(localEndPoint, remoteEndPoint, sipResponse);
                                        }
                                    }
                                    catch (SIPValidationException sipValidationException)
                                    {
                                        SIPBadResponseInTraceEvent?.Invoke(localEndPoint, remoteEndPoint, sipMessageBuffer.RawMessage, sipValidationException.SIPErrorField, sipMessageBuffer.RawMessage);
                                    }

                                    #endregion
                                }
                                else
                                {
                                    #region SIP Request.

                                    try
                                    {
                                        SIPRequest sipRequest = SIPRequest.ParseSIPRequest(sipMessageBuffer, m_sipEncoding, m_sipBodyEncoding);

                                        if (!sipRequest.IsValid(out var sipRequestErrorField, out var sipRequestValidationError))
                                        {
                                            throw new SIPValidationException(sipRequestErrorField, sipRequestValidationError);
                                        }

                                        SIPRequestInTraceEvent?.Invoke(localEndPoint, remoteEndPoint, sipRequest);

                                        // Stateful cores will create transactions once they get the request and the transport layer will use those transactions.
                                        // Stateless cores will not be affected by this step as the transaction layer will always return false.
                                        SIPTransaction requestTransaction = (m_transactionEngine != null) ? m_transactionEngine.GetTransaction(sipRequest) : null;
                                        if (requestTransaction != null)
                                        {
                                            if (requestTransaction.TransactionState == SIPTransactionStatesEnum.Completed && sipRequest.Method != SIPMethodsEnum.ACK
                                                && sipRequest.Method != SIPMethodsEnum.PRACK)
                                            {
                                                if (requestTransaction.TransactionFinalResponse != null)
                                                {
                                                    logger.LogWarning("Resending final response for {Method}, {URI}, cseq={CSeq}.", sipRequest.Method, sipRequest.URI, sipRequest.Header.CSeq);
                                                    requestTransaction.OnRetransmitFinalResponse();
                                                    return SendResponseAsync(requestTransaction.TransactionFinalResponse);
                                                }
                                            }
                                            else if (sipRequest.Method == SIPMethodsEnum.ACK)
                                            {
                                                if (requestTransaction.TransactionState == SIPTransactionStatesEnum.Completed ||
                                                    requestTransaction.TransactionState == SIPTransactionStatesEnum.Cancelled)
                                                {
                                                    sipRequest.Header.Vias.UpateTopViaHeader(remoteEndPoint.GetIPEndPoint());
                                                    requestTransaction.ACKReceived(localEndPoint, remoteEndPoint, sipRequest);
                                                }
                                                else
                                                {
                                                    SIPBadRequestInTraceEvent?.Invoke(localEndPoint, remoteEndPoint, "ACK received on " + requestTransaction.TransactionState + " transaction, ignoring.", SIPValidationFieldsEnum.Request, null);
                                                }
                                            }
                                            else if (sipRequest.Method == SIPMethodsEnum.PRACK)
                                            {
                                                sipRequest.Header.Vias.UpateTopViaHeader(remoteEndPoint.GetIPEndPoint());
                                                requestTransaction.PRACKReceived(localEndPoint, remoteEndPoint, sipRequest);
                                            }
                                            else if (sipRequest.Method == SIPMethodsEnum.INVITE && (requestTransaction.TransactionState == SIPTransactionStatesEnum.Trying ||
                                                requestTransaction.TransactionState == SIPTransactionStatesEnum.Proceeding))
                                            {
                                                return SendResponseAsync(requestTransaction.UnreliableProvisionalResponse);
                                            }
                                            else
                                            {
                                                logger.LogWarning("Transaction already exists, ignoring duplicate request, {Method} {URI}.", sipRequest.Method, sipRequest.URI.ToString());
                                            }
                                        }
                                        else if (m_transactionEngine != null && sipRequest.Method == SIPMethodsEnum.CANCEL &&
                                            GetTransaction(SIPTransaction.GetRequestTransactionId(sipRequest.Header.Vias.TopViaHeader.Branch, SIPMethodsEnum.INVITE)) != null)
                                        {
                                            UASInviteTransaction inviteTransaction = (UASInviteTransaction)GetTransaction(SIPTransaction.GetRequestTransactionId(sipRequest.Header.Vias.TopViaHeader.Branch, SIPMethodsEnum.INVITE));
                                            if (inviteTransaction != null)
                                            {
                                                // Note: this will generate the INVITE request response.
                                                inviteTransaction.CancelCall(sipRequest);

                                                // Note: this will generate the CANCEL request response.
                                                SIPResponse okResponse = SIPResponse.GetResponse(sipRequest, SIPResponseStatusCodesEnum.Ok, null);
                                                okResponse.Header.To.ToTag = inviteTransaction.LocalTag;
                                                // Only update response here, as INVITE requests header is already set correctly from prior responses
                                                okResponse.Header.Vias.UpateTopViaHeader(remoteEndPoint.GetIPEndPoint());
                                                return SendResponseAsync(okResponse);
                                            }
                                            else
                                            {
                                                SIPResponse noMatchingTxResponse = SIPResponse.GetResponse(localEndPoint, remoteEndPoint, SIPResponseStatusCodesEnum.CallLegTransactionDoesNotExist, null);
                                                return SendResponseAsync(noMatchingTxResponse);
                                            }
                                        }
                                        else if (SIPTransportRequestReceived != null)
                                        {
                                            // This is a new SIP request and if the validity checks are passed it will be handed off to all subscribed new request listeners
                                            if (sipRequest.Header.MaxForwards == 0 && sipRequest.Method != SIPMethodsEnum.OPTIONS)
                                            {
                                                // Check the MaxForwards value, if equal to 0 the request must be discarded. If MaxForwards is -1 it indicates the
                                                // header was not present in the request and that the MaxForwards check should not be undertaken.
                                                SIPBadRequestInTraceEvent?.Invoke(localEndPoint, remoteEndPoint, $"Zero MaxForwards on {sipRequest.Method} {sipRequest.URI} from {sipRequest.Header.From.FromURI.User} {remoteEndPoint}.", SIPValidationFieldsEnum.Request, sipRequest.ToString());
                                                SIPResponse tooManyHops = SIPResponse.GetResponse(sipRequest, SIPResponseStatusCodesEnum.TooManyHops, null);
                                                return SendResponseAsync(tooManyHops);
                                            }
                                            else if (sipRequest.Header.UnknownRequireExtension != null)
                                            {
                                                // The sender requires an extension that we don't support.
                                                SIPBadRequestInTraceEvent?.Invoke(localEndPoint, remoteEndPoint, $"Rejecting request to one or more required extensions not being supported, unsupported extensions: {sipRequest.Header.UnknownRequireExtension}.", SIPValidationFieldsEnum.Request, sipRequest.ToString());
                                                SIPResponse badRequireResp = SIPResponse.GetResponse(sipRequest, SIPResponseStatusCodesEnum.BadExtension, null);
                                                badRequireResp.Header.Unsupported = sipRequest.Header.UnknownRequireExtension;
                                                return SendResponseAsync(badRequireResp);
                                            }

                                            if (sipRequest.Header.Routes.Length > 0)
                                            {
                                                PreProcessRouteInfo(sipRequest);
                                            }

                                            // Request has passed validity checks, adjust the client Via header to reflect the socket the request was received on.
                                            sipRequest.Header.Vias.UpateTopViaHeader(remoteEndPoint.GetIPEndPoint());

                                            // Stateful cores should create a transaction once they receive this event, stateless cores should not.
                                            SIPTransportRequestReceived(localEndPoint, remoteEndPoint, sipRequest);
                                        }
                                    }
                                    catch (SIPValidationException sipRequestExcp)
                                    {
                                        SIPBadRequestInTraceEvent?.Invoke(localEndPoint, remoteEndPoint, sipRequestExcp.Message, sipRequestExcp.SIPErrorField, sipMessageBuffer.RawMessage);
                                        SIPResponse errorResponse = SIPResponse.GetResponse(localEndPoint, remoteEndPoint, sipRequestExcp.SIPResponseErrorCode, sipRequestExcp.Message);
                                        return SendResponseAsync(errorResponse);
                                    }

                                    #endregion
                                }
                            }
                            else
                            {
                                SIPBadRequestInTraceEvent?.Invoke(localEndPoint, remoteEndPoint, "Not parseable as SIP message.", SIPValidationFieldsEnum.Unknown, rawSIPMessage);
                            }
                        }
                    }
                }

                return Task.FromResult(SocketError.Success);
            }
            catch (Exception excp)
            {
                Log.Logger.LogError(excp, "Exception SIPMessageReceived. {ErrorMessage}", excp.Message);
                SIPBadRequestInTraceEvent?.Invoke(localEndPoint, remoteEndPoint, "Exception SIPTransport. " + excp.Message, SIPValidationFieldsEnum.Unknown, rawSIPMessage);
                return Task.FromResult(SocketError.Fault);
            }
        }

        /// <summary>
        /// Attempts to locate a SIP channel that can be used to communicate with a remote end point
        /// over a specific SIP protocol.
        /// </summary>
        /// <param name="protocol">The SIP protocol required for the communication.</param>
        /// <param name="dst">The destination end point.</param>
        /// <param name="channelIDHint">An optional channel ID that gives a hint as to the preferred 
        /// channel to select.</param>
        /// <param name="isForResponse">True if the channel is needed for a SIP response. New channels will not be
        /// created to send responses.</param>
        /// <returns>If found a SIP channel or null if not.</returns>
        private SIPChannel GetSIPChannelForDestination(SIPProtocolsEnum protocol, IPEndPoint dst, string channelIDHint, bool isForResponse)
        {
            if (m_sipChannels == null || m_sipChannels.Count == 0)
            {
                if (CanCreateMissingChannels && !isForResponse)
                {
                    var sipChannel = CreateChannel(protocol, dst.AddressFamily);
                    if (sipChannel != null)
                    {
                        AddSIPChannel(sipChannel);
                        return sipChannel;
                    }
                }

                throw new ApplicationException("The transport layer does not have any SIP channels.");
            }
            else if (!m_sipChannels.Any(x => x.Value.IsProtocolSupported(protocol) && x.Value.IsAddressFamilySupported(dst.Address.AddressFamily)))
            {
                if (CanCreateMissingChannels && !isForResponse)
                {
                    var sipChannel = CreateChannel(protocol, dst.AddressFamily);
                    if (sipChannel != null)
                    {
                        AddSIPChannel(sipChannel);
                        return sipChannel;
                    }
                }

                throw new ApplicationException($"The transport layer does not have any SIP channels matching {protocol} and {dst.AddressFamily}.");
            }
            else if (!string.IsNullOrEmpty(channelIDHint) && m_sipChannels.Any(x => x.Value.IsProtocolSupported(protocol) && x.Key == channelIDHint))
            {
                return m_sipChannels[channelIDHint];
            }
            else
            {
                // There's at least one channel available. If there's an IPAddress.Any channel choose that first
                // since it's able to use all the machine's active network interfaces and should be able to reach
                // any remote end point.
                IPAddress addrAny = (dst.AddressFamily == AddressFamily.InterNetworkV6) ? IPAddress.IPv6Any : IPAddress.Any;
                var matchingChannel = GetSIPChannel(protocol, addrAny);
                if (matchingChannel != null)
                {
                    return matchingChannel;
                }

                // Check for an exact match on the destination address and a SIP channel. Barring duplicate IP addresses and other 
                // shenanigans this would mean we're on the same machine. Note this will also catch loopback to loopback cases.
                matchingChannel = GetSIPChannel(protocol, dst.Address);
                if (matchingChannel != null)
                {
                    return matchingChannel;
                }

                // Now we'll rely on the Operating Systems routing table to tell us which local IP address would be the one 
                // chosen to communicate with the destination. And then look for an exact match on a channel listening address.
                IPAddress srcAddr = NetServices.GetLocalAddressForRemote(dst.Address);
                if (srcAddr != null)
                {
                    matchingChannel = GetSIPChannel(protocol, srcAddr);
                    if (matchingChannel != null)
                    {
                        return matchingChannel;
                    }
                }

                // Now we're clutching at straws. Try the IP address the OS routing table tells us is used for accessing the Internet.
                IPAddress internetSrcAddr = NetServices.InternetDefaultAddress;
                if (internetSrcAddr != null)
                {
                    matchingChannel = GetSIPChannel(protocol, internetSrcAddr);
                    if (matchingChannel != null)
                    {
                        return matchingChannel;
                    }
                }

                return m_sipChannels.Where(x => x.Value.IsProtocolSupported(protocol) && x.Value.IsAddressFamilySupported(dst.Address.AddressFamily))
                    .Select(x => x.Value).FirstOrDefault();
            }
        }

        /// <summary>
        /// Helper method for GetSIPChannelForDestination to do the SIP channel match check when it is known
        /// exactly which SIP protocol and listening IP address we're after.
        /// </summary>
        /// <param name="protocol">The SIP protocol to find a match for.</param>
        /// <param name="listeningAddress">The listening IP address to find a match for.</param>
        /// <returns>A SIP channel if a match is found or null if not.</returns>
        private SIPChannel GetSIPChannel(SIPProtocolsEnum protocol, IPAddress listeningAddress)
        {
            if (m_sipChannels.Any(x => x.Value.IsProtocolSupported(protocol) && listeningAddress.Equals(x.Value.ListeningIPAddress)))
            {
                return m_sipChannels.Where(x =>
                             x.Value.IsProtocolSupported(protocol) && listeningAddress.Equals(x.Value.ListeningIPAddress))
                           .Select(x => x.Value)
                           .First();
            }
            else
            {
                return null;
            }
        }

        /// <summary>
        /// Gets a list of this transport's SIP channels.
        /// </summary>
        /// <returns>A list of SIP channels.</returns>
        public List<SIPChannel> GetSIPChannels()
        {
            return m_sipChannels.Select(x => x.Value).ToList();
        }

        // /// <summary>
        // /// Add a SIP transaction to the transaction engine for reliable request and response delivery.
        // /// </summary>
        // /// <param name="transaction">The transaction to add.</param>
        //public void AddTransaction(SIPTransaction transaction)
        //{
        //    if (m_transactionEngine == null)
        //    {
        //        logger.LogWarning("The SIP transport was requested to add a transaction in stateless mode (noop).");
        //    }
        //    else
        //    {
        //        m_transactionEngine.AddTransaction(transaction);
        //    }
        //}

        /// <summary>
        /// Attempts to retrieve the transaction matching the supplied ID.
        /// </summary>
        /// <param name="transactionId">The transaction ID to match.</param>
        /// <returns>If found a transaction object or null if not.</returns>
        private SIPTransaction GetTransaction(string transactionId)
        {
            return m_transactionEngine?.GetTransaction(transactionId);
        }

        /// <summary>
        /// Creates an on demand SIP channel suitable for outbound connections.
        /// </summary>
        /// <param name="protocol">The transport protocol of the SIP channel to create.</param>
        /// <param name="addressFamily">Whether the channel should be created for IPv4 or IPv6.</param>
        /// <param name="port">Optional. If specified channels that open a listener will attempt to 
        /// use this port.</param>
        /// <returns>A SIP channel if it was possible to create or null if not.</returns>
        public SIPChannel CreateChannel(SIPProtocolsEnum protocol, AddressFamily addressFamily, int port = 0)
        {
            SIPChannel sipChannel = null;
            IPAddress localAddress = (addressFamily == AddressFamily.InterNetworkV6) ? IPAddress.IPv6Any : IPAddress.Any;

            switch (protocol)
            {
                case SIPProtocolsEnum.tcp:
                    sipChannel = new SIPTCPChannel(new IPEndPoint(localAddress, port));
                    break;
                case SIPProtocolsEnum.tls:
                    // Create a client only TLS channel.
                    sipChannel = new SIPTLSChannel(new IPEndPoint(localAddress, port));
                    break;
                case SIPProtocolsEnum.udp:
                    sipChannel = new SIPUDPChannel(new IPEndPoint(localAddress, port));
                    break;
                case SIPProtocolsEnum.ws:
                case SIPProtocolsEnum.wss:
                    sipChannel = new SIPClientWebSocketChannel();
                    break;
                default:
                    logger.LogWarning("Don't know how to create SIP channel for transport {protocol}.", protocol);
                    break;
            }

            return sipChannel;
        }

        /// <summary>
        /// Public wrapper for the SIP DNS lookup call being used by this SIP transport.
        /// </summary>
        /// <param name="uri">The SIP URI to resolve.</param>
        /// <returns>If successful a SIP end point for the SIP URI. For failures SIPEndPoint.Empty.</returns>
        public Task<SIPEndPoint> ResolveSIPUriAsync(SIPURI uri)
        {
            return ResolveSIPUriCallbackAsync(uri, PreferIPv6NameResolution, m_cts.Token);
        }

        public void Dispose()
        {
            Shutdown();
        }

        /// <summary>
        /// Helper method to enable logging of SIP request, responses and retransmits.
        /// </summary>
        public void EnableTraceLogs()
        {
            SIPRequestInTraceEvent += (localEP, remoteEP, req) =>
            {
                logger.LogDebug("Request received: {LocalEP}<-{RemoteEP} {StatusLine}", localEP, remoteEP, req.StatusLine);
                logger.LogTrace("Request: {Request}", req.ToString());
            };

            SIPRequestOutTraceEvent += (localEP, remoteEP, req) =>
            {
                logger.LogDebug("Request sent: {LocalEP}->{RemoteEP} {StatusLine}", localEP, remoteEP, req.StatusLine);
                logger.LogTrace("Request sent: {Request}", req.ToString());
            };

            SIPResponseInTraceEvent += (localEP, remoteEP, resp) =>
            {
                logger.LogDebug("Response received: {LocalEP}<-{RemoteEP} {ShortDescription}", localEP, remoteEP, resp.ShortDescription);
                logger.LogTrace("Response received: {Response}", resp.ToString());
            };

            SIPResponseOutTraceEvent += (localEP, remoteEP, resp) =>
            {
                logger.LogDebug("Response sent: {LocalEP}->{RemoteEP} {ShortDescription}", localEP, remoteEP, resp.ShortDescription);
                logger.LogTrace("Response sent: {Response}", resp.ToString());
            };

            SIPRequestRetransmitTraceEvent += (tx, req, count) =>
            {
                logger.LogDebug("Request retransmit {Count} for request {StatusLine}, initial transmit {InitialTransmit}s ago.", count, req.StatusLine, DateTime.Now.Subtract(tx.InitialTransmit).TotalSeconds.ToString("0.###"));
                logger.LogTrace("Request retransmitted: {Request}", req.ToString());
            };

            SIPResponseRetransmitTraceEvent += (tx, resp, count) =>
            {
                logger.LogDebug("Response retransmit {Count} for response {ShortDescription}, initial transmit {InitialTransmit}s ago.", count, resp.ShortDescription, DateTime.Now.Subtract(tx.InitialTransmit).TotalSeconds.ToString("0.###"));
                logger.LogTrace("Response retransmitted: {Response}", resp.ToString());
            };
        }
    }
}
