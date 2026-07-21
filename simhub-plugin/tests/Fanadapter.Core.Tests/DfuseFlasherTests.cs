using System;
using System.Collections.Generic;
using System.Linq;
using Fanadapter.Core;
using Xunit;

namespace Fanadapter.Core.Tests
{
    /// <summary>
    /// A scripted ROM bootloader: enough DfuSe to satisfy the flasher, recording every control
    /// transfer so the tests assert the exact wire sequence. State handling mirrors the real ROM:
    /// commands and data blocks answer dfuDNBUSY once, then dfuDNLOAD_IDLE.
    /// </summary>
    internal sealed class FakeDfuConnection : IDfuConnection
    {
        public readonly List<string> Log = new List<string>();
        public readonly List<(uint Address, byte[] Data)> Writes = new List<(uint, byte[])>();
        public readonly List<uint> ErasedSectors = new List<uint>();

        public string InterfaceName { get; set; } = "@Internal Flash   /0x08000000/16*128Kg";

        /// <summary>Initial DFU state; lets tests start from dfuERROR or dfuDNLOAD_IDLE.</summary>
        public byte State = 2; // dfuIDLE

        /// <summary>Non-zero fails every following operation with this DFU status code.</summary>
        public byte FailWithStatus;

        private uint _addressPointer;
        private int _busyPolls; // GETSTATUS answers dfuDNBUSY this many times before settling

        public void ControlOut(byte request, ushort value, byte[] data)
        {
            switch (request)
            {
                case 1: // DFU_DNLOAD
                    if (value == 0)
                    {
                        // DfuSe command block
                        uint addr = BitConverter.ToUInt32(data, 1);
                        if (data[0] == 0x41)
                        {
                            ErasedSectors.Add(addr);
                            Log.Add($"erase {addr:X8}");
                            _busyPolls = 2; // erase takes a while — poll loop must survive it
                        }
                        else if (data[0] == 0x21)
                        {
                            _addressPointer = addr;
                            Log.Add($"setaddr {addr:X8}");
                            _busyPolls = 1;
                        }
                        State = 4; // dfuDNBUSY until polled
                    }
                    else
                    {
                        if (data.Length == 0)
                        {
                            Log.Add("leave");
                            State = 7; // dfuMANIFEST
                        }
                        else
                        {
                            Writes.Add((_addressPointer, (byte[])data.Clone()));
                            Log.Add($"write {_addressPointer:X8}+{data.Length}");
                            _busyPolls = 1;
                            State = 4;
                        }
                    }
                    break;
                case 4: // DFU_CLRSTATUS
                    Log.Add("clrstatus");
                    State = 2;
                    FailWithStatus = 0;
                    break;
                case 6: // DFU_ABORT
                    Log.Add("abort");
                    State = 2;
                    break;
                default:
                    throw new InvalidOperationException($"unexpected OUT request {request}");
            }
        }

        public byte[] ControlIn(byte request, ushort value, int length)
        {
            Assert.Equal(3, request); // DFU_GETSTATUS is the only IN the flasher uses
            if (State == 4)
            {
                if (_busyPolls-- <= 0) State = 5; // dfuDNLOAD_IDLE
            }
            byte reported = State == 4 ? (byte)4 : State;
            return new byte[] { FailWithStatus, 10, 0, 0, reported, 0 };
        }

        public void Dispose() { }
    }

    public class DfuseFlasherTests
    {
        private static byte[] MakeImage(int length)
        {
            var image = new byte[length];
            // Plausible vector table: SP in AXI RAM, reset vector in flash with the thumb bit.
            BitConverter.GetBytes(0x24080000u).CopyTo(image, 0);
            BitConverter.GetBytes(0x080002A1u).CopyTo(image, 4);
            for (int i = 8; i < length; i++) image[i] = (byte)i;
            return image;
        }

        private static DfuseFlasher Create(FakeDfuConnection fake) =>
            new DfuseFlasher(fake, _ => { }); // no real sleeping in tests

        [Fact]
        public void FlashesSmallImageWithExpectedSequence()
        {
            var fake = new FakeDfuConnection();
            var image = MakeImage(1500); // 1 sector, 2 blocks (1024 + 476)

            Create(fake).Flash(image, null);

            Assert.Equal(new[] { 0x08000000u }, fake.ErasedSectors);
            Assert.Equal(2, fake.Writes.Count);
            Assert.Equal(0x08000000u, fake.Writes[0].Address);
            Assert.Equal(1024, fake.Writes[0].Data.Length);
            Assert.Equal(0x08000400u, fake.Writes[1].Address);
            Assert.Equal(476, fake.Writes[1].Data.Length);
            Assert.Equal("leave", fake.Log.Last());
            // The written bytes reassemble into exactly the input image.
            Assert.Equal(image, fake.Writes.SelectMany(w => w.Data).ToArray());
        }

        [Fact]
        public void ErasesEverySectorTheImageCovers()
        {
            var fake = new FakeDfuConnection();
            // 128K sectors from the layout string; 300 KB ⇒ 3 sectors.
            Create(fake).Flash(MakeImage(300 * 1024), null);

            Assert.Equal(
                new[] { 0x08000000u, 0x08020000u, 0x08040000u },
                fake.ErasedSectors);
        }

        [Fact]
        public void ParsesSectorSizeFromInterfaceName()
        {
            var fake = new FakeDfuConnection
            {
                // An H750-style single 128K... use 2K pages to make the difference visible.
                InterfaceName = "@Internal Flash  /0x08000000/64*2Kg",
            };
            Create(fake).Flash(MakeImage(5000), null); // 5000 B ⇒ 3 × 2K pages

            Assert.Equal(new[] { 0x08000000u, 0x08000800u, 0x08001000u }, fake.ErasedSectors);
        }

        [Fact]
        public void FallsBackTo128KSectorsWithoutInterfaceName()
        {
            var fake = new FakeDfuConnection { InterfaceName = null };
            Create(fake).Flash(MakeImage(200 * 1024), null);

            Assert.Equal(new[] { 0x08000000u, 0x08020000u }, fake.ErasedSectors);
        }

        [Fact]
        public void RecoversFromInitialErrorState()
        {
            var fake = new FakeDfuConnection { State = 10 }; // dfuERROR
            Create(fake).Flash(MakeImage(100), null);

            Assert.Equal("clrstatus", fake.Log.First());
        }

        [Fact]
        public void AbortsBackToIdleFromDnloadIdle()
        {
            var fake = new FakeDfuConnection { State = 5 }; // dfuDNLOAD_IDLE (interrupted update)
            Create(fake).Flash(MakeImage(100), null);

            Assert.Equal("abort", fake.Log.First());
        }

        [Fact]
        public void SurfacesDfuErrorStatus()
        {
            var fake = new FakeDfuConnection { FailWithStatus = 0x0A }; // errVERIFY-ish
            var ex = Assert.Throws<InvalidOperationException>(
                () => Create(fake).Flash(MakeImage(100), null));
            Assert.Contains("status 10", ex.Message);
        }

        [Fact]
        public void ReportsMonotonicProgress()
        {
            var fake = new FakeDfuConnection();
            var seen = new List<FlashProgress>();
            Create(fake).Flash(MakeImage(4096), seen.Add);

            Assert.Equal(FlashPhase.Erase, seen.First().Phase);
            Assert.Equal(FlashPhase.Leave, seen.Last().Phase);
            Assert.Equal(1, seen.Last().Done);
            // Within each phase the counter never goes backwards.
            foreach (var phase in new[] { FlashPhase.Erase, FlashPhase.Write })
            {
                var counts = seen.Where(p => p.Phase == phase).Select(p => p.Done).ToArray();
                Assert.Equal(counts.OrderBy(c => c), counts);
            }
        }

        // ---------- suffix + plausibility ----------

        [Fact]
        public void StripsDfuSuffix()
        {
            var image = MakeImage(100);
            var suffixed = new byte[116];
            image.CopyTo(suffixed, 0);
            // DFU 1.1 suffix: ..., "UFD" at [n-8..n-6], bLength=16 at [n-5], CRC last.
            suffixed[108] = 0x55;
            suffixed[109] = 0x46;
            suffixed[110] = 0x44;
            suffixed[111] = 16;

            Assert.Equal(image, DfuseFlasher.StripDfuSuffix(suffixed));
        }

        [Fact]
        public void LeavesUnsuffixedImagesAlone()
        {
            var image = MakeImage(100);
            Assert.Same(image, DfuseFlasher.StripDfuSuffix(image));
        }

        [Theory]
        [InlineData(0x24080000u, 0x080002A1u, true)]  // AXI stack, thumb reset in flash
        [InlineData(0x20020000u, 0x08000101u, true)]  // DTCM stack
        [InlineData(0x24080000u, 0x080002A0u, false)] // thumb bit missing
        [InlineData(0x24080000u, 0x200002A1u, false)] // reset vector in RAM
        [InlineData(0x08000000u, 0x080002A1u, false)] // SP in flash — not a vector table
        public void PlausibilityCheckCatchesWrongFiles(uint sp, uint reset, bool expected)
        {
            var image = new byte[64];
            BitConverter.GetBytes(sp).CopyTo(image, 0);
            BitConverter.GetBytes(reset).CopyTo(image, 4);
            Assert.Equal(expected, DfuseFlasher.LooksLikeFirmware(image));
        }

        [Fact]
        public void RejectsImagesLargerThanBankOne()
        {
            // Anything past 1 MB would erase into bank 2 and eat the saved config.
            var image = MakeImage(0x100001);
            Assert.False(DfuseFlasher.LooksLikeFirmware(image));
        }
    }
}
