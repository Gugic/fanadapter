using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Fanadapter.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Fanadapter.Core.Tests
{
    public class SerialClientFramingTests
    {
        private static (SerialClient, FakeTransport) Connected()
        {
            var transport = new FakeTransport();
            transport.Open();
            return (new SerialClient(transport), transport);
        }

        [Fact]
        public void ReassemblesLinesSplitAcrossChunks()
        {
            var (client, transport) = Connected();
            var logs = new List<string>();
            client.LogLineReceived += logs.Add;

            transport.Receive("hello ");
            transport.Receive("world\nsecond");
            Assert.Single(logs);
            Assert.Equal("hello world", logs[0]);

            transport.Receive(" line\n");
            Assert.Equal(2, logs.Count);
            Assert.Equal("second line", logs[1]);
        }

        [Fact]
        public void StripsCarriageReturnsAndSkipsBlankLines()
        {
            var (client, transport) = Connected();
            var logs = new List<string>();
            client.LogLineReceived += logs.Add;

            transport.Receive("banner\r\n\r\n\n  \nnext\r\n");

            Assert.Equal(new[] { "banner", "next" }, logs);
        }

        [Fact]
        public void SplitsMultipleLinesArrivingInOneChunk()
        {
            var (client, transport) = Connected();
            var logs = new List<string>();
            client.LogLineReceived += logs.Add;

            transport.Receive("a\nb\nc\n");

            Assert.Equal(new[] { "a", "b", "c" }, logs);
        }

        [Fact]
        public void TreatsMalformedJsonAsALogLine()
        {
            var (client, transport) = Connected();
            var logs = new List<string>();
            client.LogLineReceived += logs.Add;

            transport.ReceiveLine("{not valid json");

            Assert.Single(logs);
        }
    }

    public class SerialClientRequestTests
    {
        private static (SerialClient, FakeTransport) Connected()
        {
            var transport = new FakeTransport();
            transport.Open();
            return (new SerialClient(transport), transport);
        }

        [Fact]
        public async Task ResolvesARequestWithTheNextNonEventReply()
        {
            var (client, transport) = Connected();

            var pending = client.SendAsync(new JObject { ["cmd"] = "version" });
            Assert.Equal("{\"cmd\":\"version\"}\n", transport.Written[0]);

            transport.ReceiveLine("{\"fw\":\"fanadapter-stm32\",\"ver\":\"0.6.0\",\"protocol\":5}");

            var reply = await pending;
            Assert.Equal("fanadapter-stm32", reply.Value<string>("fw"));
        }

        [Fact]
        public async Task MatchesRepliesInOrder()
        {
            var (client, transport) = Connected();

            var first = client.SendAsync(new JObject { ["cmd"] = "a" });
            var second = client.SendAsync(new JObject { ["cmd"] = "b" });

            transport.ReceiveLine("{\"n\":1}");
            transport.ReceiveLine("{\"n\":2}");

            Assert.Equal(1, (await first).Value<int>("n"));
            Assert.Equal(2, (await second).Value<int>("n"));
        }

        [Fact]
        public async Task EventsDoNotConsumePendingRequests()
        {
            var (client, transport) = Connected();
            LiveSlot seen = null;
            client.LiveInput += s => seen = s;

            var pending = client.SendAsync(new JObject { ["cmd"] = "version" });

            transport.ReceiveLine("{\"event\":\"live\",\"slot\":0,\"buttons\":5,\"axes\":[1,2]}");
            transport.ReceiveLine("{\"ok\":true}");

            Assert.True((await pending).Value<bool>("ok"));
            Assert.NotNull(seen);
            Assert.Equal(5u, seen.Buttons);
        }

        [Fact]
        public async Task ALateReplyIsAbsorbedByTheTimedOutRequest()
        {
            // The important guarantee: a command that gave up must still consume
            // its eventual reply, otherwise that reply gets handed to the next
            // request and every response after it is off by one.
            var (client, transport) = Connected();

            var timedOut = client.SendAsync(new JObject { ["cmd"] = "slow" }, timeoutMs: 40);
            await Assert.ThrowsAsync<TimeoutException>(() => timedOut);

            var next = client.SendAsync(new JObject { ["cmd"] = "next" });

            transport.ReceiveLine("{\"which\":\"late reply to slow\"}");
            transport.ReceiveLine("{\"which\":\"reply to next\"}");

            Assert.Equal("reply to next", (await next).Value<string>("which"));
        }

        [Fact]
        public async Task DisconnectFailsPendingRequests()
        {
            var (client, transport) = Connected();
            var pending = client.SendAsync(new JObject { ["cmd"] = "version" });

            client.Close();

            await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
        }

        [Fact]
        public async Task TransportFailureFaultsTheClientAndPendingRequests()
        {
            var (client, transport) = Connected();
            Exception faulted = null;
            client.Faulted += ex => faulted = ex;

            var pending = client.SendAsync(new JObject { ["cmd"] = "version" });
            transport.Fail(new InvalidOperationException("device removed"));

            await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
            Assert.NotNull(faulted);
        }

        [Fact]
        public async Task SendingWhileClosedFailsRatherThanQueueing()
        {
            var transport = new FakeTransport();
            var client = new SerialClient(transport);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => client.SendAsync(new JObject { ["cmd"] = "version" }));
        }
    }

    public class SerialClientResyncTests
    {
        private static (SerialClient, FakeTransport) Connected()
        {
            var transport = new FakeTransport();
            transport.Open();
            return (new SerialClient(transport), transport);
        }

        [Fact]
        public async Task ASingleStaleReplyDoesNotTriggerResync()
        {
            // A merely-late reply is fully recovered by absorbing it; probing
            // here would needlessly sacrifice whatever else was in flight.
            var (client, transport) = Connected();

            var slow = client.SendAsync(new JObject { ["cmd"] = "slow" }, timeoutMs: 40);
            await Assert.ThrowsAsync<TimeoutException>(() => slow);

            var next = client.SendAsync(new JObject { ["cmd"] = "next" });
            transport.ReceiveLine("{\"which\":\"late reply to slow\"}");
            transport.ReceiveLine("{\"which\":\"reply to next\"}");

            Assert.Equal("reply to next", (await next).Value<string>("which"));
            Assert.DoesNotContain(transport.Written, w => w.Contains("version"));
        }

        [Fact]
        public async Task ADestroyedReplyIsDetectedAndTheQueueRealigned()
        {
            // The 2026-07-21 field failure: the firmware once glued a truncated
            // event line to a reply, destroying both. With replies matched
            // strictly FIFO the queue went permanently one-behind — every
            // request timed out, every reply arrived "stale", forever. Two
            // consecutive stale absorptions must trigger a version probe that
            // realigns the stream.
            var (client, transport) = Connected();
            var logs = new List<string>();
            client.LogLineReceived += logs.Add;

            // Request A's reply is destroyed on the wire: it never arrives.
            var a = client.SendAsync(new JObject { ["cmd"] = "a" }, timeoutMs: 40);
            await Assert.ThrowsAsync<TimeoutException>(() => a);

            // B's reply is absorbed by abandoned A (stale #1), so B times out too.
            var b = client.SendAsync(new JObject { ["cmd"] = "b" }, timeoutMs: 40);
            transport.ReceiveLine("{\"which\":\"reply to b\"}");
            await Assert.ThrowsAsync<TimeoutException>(() => b);

            // C's reply is absorbed by abandoned B (stale #2) — the probe fires.
            var c = client.SendAsync(new JObject { ["cmd"] = "c" });
            transport.ReceiveLine("{\"which\":\"reply to c\"}");
            Assert.Contains(transport.Written, w => w.Contains("\"cmd\":\"version\""));

            // Anything non-probe arriving during the resync is discarded, not matched.
            transport.ReceiveLine("{\"which\":\"stray\"}");
            Assert.Contains(logs, l => l.StartsWith("[resync: discarded]"));

            // The probe's reply (the only one carrying "fw") anchors realignment;
            // C was queued before the probe, so it is failed rather than misfed.
            transport.ReceiveLine("{\"fw\":\"fanadapter-stm32\",\"ver\":\"0.6.0\",\"protocol\":5}");
            await Assert.ThrowsAsync<InvalidOperationException>(() => c);
            Assert.Contains(logs, l => l.Contains("realigned"));

            // From here on matching is correct again.
            var d = client.SendAsync(new JObject { ["cmd"] = "d" });
            transport.ReceiveLine("{\"which\":\"reply to d\"}");
            Assert.Equal("reply to d", (await d).Value<string>("which"));
        }

        [Fact]
        public async Task ASuccessBetweenStalesResetsTheStreak()
        {
            // Two isolated late replies with healthy traffic in between are two
            // independent slow commands, not a skew.
            var (client, transport) = Connected();

            var slow1 = client.SendAsync(new JObject { ["cmd"] = "slow1" }, timeoutMs: 40);
            await Assert.ThrowsAsync<TimeoutException>(() => slow1);
            transport.ReceiveLine("{\"which\":\"late 1\"}"); // stale #1

            var ok = client.SendAsync(new JObject { ["cmd"] = "ok" });
            transport.ReceiveLine("{\"which\":\"ok\"}");
            Assert.Equal("ok", (await ok).Value<string>("which")); // streak reset

            var slow2 = client.SendAsync(new JObject { ["cmd"] = "slow2" }, timeoutMs: 40);
            await Assert.ThrowsAsync<TimeoutException>(() => slow2);
            transport.ReceiveLine("{\"which\":\"late 2\"}"); // stale #1 again

            Assert.DoesNotContain(transport.Written, w => w.Contains("version"));
        }
    }

    public class LiveEventParsingTests
    {
        [Fact]
        public void AbsentHatMeansTheDeviceHasNone()
        {
            var live = SerialClient.ParseLive(JObject.Parse("{\"slot\":0,\"buttons\":0,\"axes\":[]}"));
            Assert.False(live.HasHat);
            Assert.Null(live.Hat);
        }

        [Fact]
        public void NegativeHatMeansCentred()
        {
            // Distinct from "no hat at all": the control exists and is released.
            var live = SerialClient.ParseLive(JObject.Parse("{\"slot\":0,\"buttons\":0,\"hat\":-1}"));
            Assert.True(live.HasHat);
            Assert.Null(live.Hat);
        }

        [Fact]
        public void DirectionalHatIsCarriedThrough()
        {
            var live = SerialClient.ParseLive(JObject.Parse("{\"slot\":0,\"buttons\":0,\"hat\":3}"));
            Assert.True(live.HasHat);
            Assert.Equal(3, live.Hat);
        }

        [Fact]
        public void ButtonsUseTheFullUnsignedRange()
        {
            // Bit 31 set — this must not come back negative or the top button
            // silently stops working.
            var live = SerialClient.ParseLive(JObject.Parse("{\"slot\":0,\"buttons\":4294967295}"));
            Assert.Equal(uint.MaxValue, live.Buttons);
        }

        [Fact]
        public void KeysAreCarriedForKeyboards()
        {
            var live = SerialClient.ParseLive(JObject.Parse("{\"slot\":1,\"buttons\":0,\"keys\":[4,0,0,0,0,0]}"));
            Assert.Equal(new[] { 4, 0, 0, 0, 0, 0 }, live.Keys);
        }
    }
}
