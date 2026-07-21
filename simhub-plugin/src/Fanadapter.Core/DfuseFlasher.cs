using System;
using System.Threading;

namespace Fanadapter.Core
{
    /// <summary>
    /// The DFU control-transfer pipe under <see cref="DfuseFlasher"/>. Abstracted so the DfuSe
    /// state machine can be tested against a scripted fake without hardware — the same split
    /// ISerialTransport gives SerialClient. The real implementation is WinUsbDfuDevice.
    /// </summary>
    public interface IDfuConnection : IDisposable
    {
        /// <summary>Class-type, interface-directed control OUT (the DFU request set).</summary>
        void ControlOut(byte request, ushort value, byte[] data);

        /// <summary>Class-type, interface-directed control IN; returns exactly the bytes read.</summary>
        byte[] ControlIn(byte request, ushort value, int length);

        /// <summary>
        /// The DfuSe alt-0 interface string ("@Internal Flash /0x08000000/16*128Kg"), or null when
        /// unavailable. Only the sector size is parsed out of it.
        /// </summary>
        string InterfaceName { get; }
    }

    public enum FlashPhase
    {
        Erase,
        Write,
        Leave,
    }

    public struct FlashProgress
    {
        /// <summary>Erase counts sectors; Write counts bytes.</summary>
        public FlashPhase Phase;
        public int Done;
        public int Total;
    }

    /// <summary>
    /// DfuSe (ST extensions, AN3156) driver for the STM32 ROM bootloader — the C# twin of
    /// webconfig's src/lib/dfu.ts, and deliberately NOT a general DFU implementation: the ROM's
    /// fixed properties are relied on (wTransferSize = 1024, alt 0 = Internal Flash) instead of
    /// discovered. The block-transfer pattern mirrors the proven webdfu approach: Set Address
    /// Pointer before every data block and always send wBlockNum = 2, so the write offset is
    /// exactly the address pointer and no block-counter arithmetic exists to get wrong. Keep the
    /// two implementations in step — a divergence means the two clients flash differently.
    /// </summary>
    public class DfuseFlasher
    {
        /// <summary>Where the application lives; erase and write both start here.</summary>
        public const uint AppBase = 0x08000000;

        // DFU 1.1 bRequest values
        private const byte DFU_DNLOAD = 1;
        private const byte DFU_GETSTATUS = 3;
        private const byte DFU_CLRSTATUS = 4;
        private const byte DFU_ABORT = 6;

        // DFU device states (the ones we branch on)
        private const byte STATE_DFU_IDLE = 2;
        private const byte STATE_DNBUSY = 4;
        private const byte STATE_DNLOAD_IDLE = 5;
        private const byte STATE_MANIFEST = 7;
        private const byte STATE_ERROR = 10;

        // DfuSe command bytes (DNLOAD with wValue = 0)
        private const byte CMD_SET_ADDRESS = 0x21;
        private const byte CMD_ERASE = 0x41;

        // The ST ROM's fixed transfer size (AN3156). Reading it from the DFU functional
        // descriptor would only re-discover this constant with extra failure modes.
        private const int TransferSize = 1024;

        private readonly IDfuConnection _dfu;

        /// <summary>Injectable for tests so erase polling doesn't sleep for real.</summary>
        private readonly Action<int> _sleep;

        /// <summary>128 KB on every H743 sector; parsed from the interface string when present.</summary>
        private int _sectorSize = 128 * 1024;

        public DfuseFlasher(IDfuConnection dfu, Action<int> sleep = null)
        {
            _dfu = dfu;
            _sleep = sleep ?? Thread.Sleep;
            ParseSectorSize();
        }

        /// <summary>
        /// PlatformIO appends a 16-byte DFU suffix to firmware.bin (for dfu-util's device
        /// matching); raw objcopy output has none. The suffix is metadata, not code — strip it so
        /// it never lands in flash. Layout (DFU 1.1 appendix B): ucDfuSignature = "UFD" at
        /// [len-8..len-6], bLength = 16 at [len-5], dwCRC last.
        /// </summary>
        public static byte[] StripDfuSuffix(byte[] image)
        {
            int n = image.Length;
            if (n < 16) return image;
            if (image[n - 8] != 0x55 || image[n - 7] != 0x46 || image[n - 6] != 0x44) return image;
            int suffixLen = image[n - 5];
            if (suffixLen < 16 || suffixLen > n) return image;
            var stripped = new byte[n - suffixLen];
            Array.Copy(image, stripped, stripped.Length);
            return stripped;
        }

        /// <summary>
        /// Cheap plausibility check before erasing anything: a Cortex-M image starts with the
        /// initial stack pointer (must land in some RAM alias) and the reset vector (must land in
        /// flash, thumb bit set). Catches "picked the wrong file".
        /// </summary>
        public static bool LooksLikeFirmware(byte[] image)
        {
            if (image.Length < 8 || image.Length > 0x100000) return false; // > bank 1 would eat the config bank
            uint sp = BitConverter.ToUInt32(image, 0);
            uint reset = BitConverter.ToUInt32(image, 4);
            bool spOk = false;
            foreach (uint ramBase in new uint[] { 0x20000000, 0x24000000, 0x30000000, 0x38000000 })
            {
                if (sp >= ramBase && sp <= ramBase + 0x100000) { spOk = true; break; }
            }
            bool resetOk = (reset & 0xFF000000) == 0x08000000 && (reset & 1) == 1;
            return spOk && resetOk;
        }

        public void Flash(byte[] imageIn, Action<FlashProgress> onProgress)
        {
            var image = StripDfuSuffix(imageIn);

            EnsureIdle();

            // Erase exactly the sectors the image covers. Everything above — including the config
            // in flash bank 2 — is untouched, which is what makes an update non-destructive.
            int sectorCount = (image.Length + _sectorSize - 1) / _sectorSize;
            for (int i = 0; i < sectorCount; i++)
            {
                Report(onProgress, FlashPhase.Erase, i, sectorCount);
                DfuseCommand(CMD_ERASE, AppBase + (uint)(i * _sectorSize));
            }
            Report(onProgress, FlashPhase.Erase, sectorCount, sectorCount);

            // Write. Address pointer is re-set before every block (see class comment).
            for (int offset = 0; offset < image.Length; offset += TransferSize)
            {
                Report(onProgress, FlashPhase.Write, offset, image.Length);
                int len = Math.Min(TransferSize, image.Length - offset);
                var chunk = new byte[len];
                Array.Copy(image, offset, chunk, 0, len);
                DfuseCommand(CMD_SET_ADDRESS, AppBase + (uint)offset);
                _dfu.ControlOut(DFU_DNLOAD, 2, chunk);
                PollUntil(STATE_DNLOAD_IDLE);
            }
            Report(onProgress, FlashPhase.Write, image.Length, image.Length);

            // Leave: point at the app, zero-length download, final GETSTATUS kicks the manifest.
            // The ROM jumps to the app and drops off the bus mid-transfer — errors past this
            // point mean success.
            Report(onProgress, FlashPhase.Leave, 0, 1);
            DfuseCommand(CMD_SET_ADDRESS, AppBase);
            try
            {
                _dfu.ControlOut(DFU_DNLOAD, 2, new byte[0]);
                var st = GetStatus();
                if (st.State != STATE_MANIFEST && st.Status != 0)
                {
                    throw new InvalidOperationException(
                        $"unexpected manifest state {st.State}/{st.Status}");
                }
            }
            catch (Exception ex) when (!(ex is InvalidOperationException))
            {
                // Device already rebooted into the app — the expected outcome.
            }
            Report(onProgress, FlashPhase.Leave, 1, 1);
        }

        // ---------- protocol plumbing ----------

        private struct DfuStatus
        {
            public byte Status;
            public int PollTimeout;
            public byte State;
        }

        private void ParseSectorSize()
        {
            string name = _dfu.InterfaceName;
            if (name == null) return;
            // "@Internal Flash   /0x08000000/16*128Kg"
            var m = System.Text.RegularExpressions.Regex.Match(
                name, @"/0x[0-9a-fA-F]+/\d+\*(\d+)([KM])");
            if (!m.Success) return;
            int unit = m.Groups[2].Value == "M" ? 1024 * 1024 : 1024;
            _sectorSize = int.Parse(m.Groups[1].Value) * unit;
        }

        private void EnsureIdle()
        {
            var st = GetStatus();
            if (st.State == STATE_ERROR)
            {
                _dfu.ControlOut(DFU_CLRSTATUS, 0, new byte[0]);
                st = GetStatus();
            }
            if (st.State != STATE_DFU_IDLE)
            {
                _dfu.ControlOut(DFU_ABORT, 0, new byte[0]);
                st = GetStatus();
            }
            if (st.State != STATE_DFU_IDLE)
            {
                throw new InvalidOperationException(
                    $"bootloader stuck in DFU state {st.State} (status {st.Status})");
            }
        }

        /// <summary>DNLOAD with wValue = 0 is a DfuSe command block: opcode + LE address.</summary>
        private void DfuseCommand(byte op, uint address)
        {
            var buf = new byte[5];
            buf[0] = op;
            buf[1] = (byte)address;
            buf[2] = (byte)(address >> 8);
            buf[3] = (byte)(address >> 16);
            buf[4] = (byte)(address >> 24);
            _dfu.ControlOut(DFU_DNLOAD, 0, buf);
            // Commands report completion through GETSTATUS: dfuDNBUSY with a real bwPollTimeout
            // (a 128K sector erase runs seconds), then dfuDNLOAD_IDLE.
            PollUntil(STATE_DNLOAD_IDLE);
        }

        private DfuStatus GetStatus()
        {
            var b = _dfu.ControlIn(DFU_GETSTATUS, 0, 6);
            if (b == null || b.Length < 6)
            {
                throw new InvalidOperationException("GETSTATUS returned a short read");
            }
            return new DfuStatus
            {
                Status = b[0],
                PollTimeout = b[1] | (b[2] << 8) | (b[3] << 16),
                State = b[4],
            };
        }

        private void PollUntil(byte wanted)
        {
            var st = GetStatus();
            while (st.State == STATE_DNBUSY)
            {
                _sleep(st.PollTimeout > 0 ? st.PollTimeout : 5);
                st = GetStatus();
            }
            if (st.Status != 0)
            {
                throw new InvalidOperationException($"DFU error: status {st.Status}, state {st.State}");
            }
            if (st.State != wanted)
            {
                throw new InvalidOperationException($"unexpected DFU state {st.State} (wanted {wanted})");
            }
        }

        private static void Report(Action<FlashProgress> cb, FlashPhase phase, int done, int total)
        {
            cb?.Invoke(new FlashProgress { Phase = phase, Done = done, Total = total });
        }
    }
}
