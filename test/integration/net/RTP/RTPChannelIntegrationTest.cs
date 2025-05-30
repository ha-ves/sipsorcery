﻿//-----------------------------------------------------------------------------
// Filename: RTPSChannelUnitTest.cs
//
// Description: Integration tests for the RTPChannel class.
//
// Author(s):
// Aaron Clauson (aaron@sipsorcery.com)
//
// History:
// 01 May 2020	Aaron Clauson	Created, Dublin, Ireland.
// 24 Jul 2020	Aaron Clauson	Extracted unit test that was regularly failing
//                              on ubuntu and macos.
//
// License: 
// BSD 3-Clause "New" or "Revised" License, see included LICENSE.md file.
//-----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Xunit;

namespace SIPSorcery.Net.UnitTests
{
    [Trait("Category", "integration")]
    public class RTPChannelIntegrationTest
    {
        private const int TEST_TIMEOUT_SECONDS = 2;

        private Microsoft.Extensions.Logging.ILogger logger = null;

        public RTPChannelIntegrationTest(Xunit.Abstractions.ITestOutputHelper output)
        {
            logger = SIPSorcery.UnitTests.TestLogHelper.InitTestLogger(output);
        }

        /// <summary>
        /// Tests that multiple pairs of RTP channels can communicate.
        /// </summary>
        [Fact]
        public async Task MultipleRtpChannelLoopbackUnitTest()
        {
            logger.LogDebug("--> {MethodName}", System.Reflection.MethodBase.GetCurrentMethod().Name);
            logger.BeginScope(System.Reflection.MethodBase.GetCurrentMethod().Name);

            const int PACKET_LENGTH = 100;

            List<Task> tasks = new List<Task>();

            for (int i = 0; i < 3; i++)
            {
                var t = Task.Run(async () =>
                {
                    RTPChannel channel1 = new RTPChannel(false, null);

                    bool testResult = false;
                    ManualResetEventSlim testCompleteEvent = new ManualResetEventSlim(false);

                    RTPChannel channel2 = new RTPChannel(false, null);
                    channel2.OnRTPDataReceived += (lep, rep, pkt) =>
                    {
                        logger.LogDebug("RTP data receive packet length {PacketLength}.", pkt.Length);
                        testResult = pkt.Length == PACKET_LENGTH;
                        testCompleteEvent.Set();
                    };

                    channel1.Start();
                    channel2.Start();

                    // Give the socket receive tasks time to fire up.
                    await Task.Delay(2000).ConfigureAwait(false);

                    IPAddress channel2Address = (channel2.RTPLocalEndPoint.AddressFamily == AddressFamily.InterNetworkV6) ? IPAddress.IPv6Loopback : IPAddress.Loopback;
                    IPEndPoint channel2Dst = new IPEndPoint(channel2Address, channel2.RTPPort);

                    logger.LogDebug("Attempting to send packet from {RTPLocalEndPoint} to {channel2Dst}.", channel1.RTPLocalEndPoint, channel2Dst);

                    var sendResult = channel1.Send(RTPChannelSocketsEnum.RTP, channel2Dst, new byte[PACKET_LENGTH]);

                    logger.LogDebug("Send result {sendResult}.", sendResult);

                    testCompleteEvent.Wait(TimeSpan.FromSeconds(TEST_TIMEOUT_SECONDS));

                    Assert.True(testResult);

                    channel1.Close("normal");
                    channel2.Close("normal");
                });

                tasks.Add(t);
            }

            var timeoutTask = Task.Delay(TimeSpan.FromMilliseconds(10000));
            var winner = await Task.WhenAny(tasks.Concat(new[] { timeoutTask }));

            if (winner == timeoutTask)
            {
                Assert.Fail($"Test timed out after 10s.");
            }

            logger.LogDebug("Test complete.");
        }
    }
}
