#!/usr/bin/env python3
"""
DTR-safe STM32 USART ROM-bootloader flasher (AN3155) for the STM32H743 port.

Why this exists instead of STM32CubeProgrammer:
  We flash the STM32 over the ESP32-S3 UART bridge (board PA9/PA10 <-> S3 <-> CH340 <-> COM port).
  CubeProgrammer / stm32flash assert DTR (and pulse RTS) when they open the port, which trips the
  CH340 auto-reset circuit on the S3 dev board and drops the bridge mid-flash. This tool keeps DTR
  and RTS deasserted, opens the port once, waits out the (one) S3 reboot the open still causes, then
  speaks AN3155 directly. It controls every byte, so timing/parity are predictable.

Bootloader entry (pick one):
  --enter manual   You put the STM32 in the ROM bootloader yourself (hold BOOT0, tap NRST) BEFORE
                   running this. Use this to PROVE the bridge+protocol path works end to end.
  --enter dfu      We send "dfu\n" on the console first so the firmware jumps to the bootloader
                   itself (hands-free). Only works once the programmatic jump is reliable.

Wire format is fixed by the ROM: 8 data bits, EVEN parity, 1 stop bit. The S3 bridge mirrors that.

Usage:
  python stm32_uart_flash.py --port COM16 --bin .pio/build/weact_h743/firmware.bin --enter manual --go
  python stm32_uart_flash.py --port COM16 --bin firmware.bin --enter dfu --go     # after jump is fixed
  python stm32_uart_flash.py --port COM16 --probe                                 # just sync + Get + Get-ID

Requires: pyserial  (pip install pyserial)
"""

import argparse
import sys
import time

try:
    import serial  # pyserial
except ImportError:
    sys.exit("pyserial is required:  python -m pip install pyserial")

ACK = 0x79
NACK = 0x1F
INIT = 0x7F

H743_PID = 0x450          # STM32H743/753 product ID returned by Get-ID
FLASH_BASE = 0x08000000
WRITE_CHUNK = 256         # AN3155 max bytes per Write Memory frame
FLASHWORD = 32            # H7 programs in 256-bit (32-byte) flashwords -> pad image to a multiple


def log(msg):
    print(msg, flush=True)


class BLError(Exception):
    pass


class STBootloader:
    def __init__(self, port, baud, verbose=False):
        self.verbose = verbose
        # DTR/RTS must stay deasserted so we never trip the S3's auto-reset circuit.
        # We do NOT let pyserial drive them: dsrdtr/rtscts off, then force both low.
        self.ser = serial.Serial()
        self.ser.port = port
        self.ser.baudrate = baud
        self.ser.bytesize = serial.EIGHTBITS
        self.ser.parity = serial.PARITY_EVEN     # 8E1 — fixed by the ROM bootloader
        self.ser.stopbits = serial.STOPBITS_ONE
        self.ser.timeout = 1.0
        self.ser.write_timeout = 5.0
        self.ser.rtscts = False
        self.ser.dsrdtr = False
        # Set the control lines low BEFORE open where the platform allows; also re-assert after.
        try:
            self.ser.dtr = False
            self.ser.rts = False
        except Exception:
            pass

    def open(self, settle_s):
        self.ser.open()
        # Opening the CH340 still toggles its lines once, rebooting the S3. Keep our lines low and
        # wait for the bridge to come back before we touch the wire.
        self.ser.dtr = False
        self.ser.rts = False
        log(f"[port] opened {self.ser.port} @ {self.ser.baudrate} 8E1, DTR/RTS low")
        if settle_s > 0:
            log(f"[port] waiting {settle_s:.1f}s for the S3 bridge to reboot/settle...")
            time.sleep(settle_s)
        self.ser.reset_input_buffer()
        self.ser.reset_output_buffer()

    def close(self):
        try:
            self.ser.close()
        except Exception:
            pass

    # ---- low level -------------------------------------------------------
    def _w(self, data):
        if isinstance(data, int):
            data = bytes([data])
        self.ser.write(data)

    def _r(self, n, timeout=None):
        if timeout is not None:
            old = self.ser.timeout
            self.ser.timeout = timeout
            try:
                return self.ser.read(n)
            finally:
                self.ser.timeout = old
        return self.ser.read(n)

    def _ack(self, timeout=2.0, what="ack"):
        b = self._r(1, timeout=timeout)
        if not b:
            raise BLError(f"timeout waiting for {what} (no byte)")
        v = b[0]
        if v == ACK:
            if self.verbose:
                log(f"    <- ACK ({what})")
            return True
        if v == NACK:
            raise BLError(f"NACK (0x1F) on {what}")
        raise BLError(f"unexpected 0x{v:02X} on {what} (wanted ACK 0x79)")

    def _cmd(self, cmd, timeout=2.0):
        # Every command byte is followed by its complement.
        self._w(bytes([cmd, cmd ^ 0xFF]))
        self._ack(timeout=timeout, what=f"cmd 0x{cmd:02X}")

    @staticmethod
    def _xor(data):
        c = 0
        for b in data:
            c ^= b
        return c

    # ---- protocol --------------------------------------------------------
    def sync(self, wait_s=0.0):
        """Send 0x7F until the ROM autobauds and ACKs (or NACKs = already synced).
        wait_s > 0 keeps polling for that long, so you can launch the command and THEN
        press BOOT0+RST on the board — it auto-detects the bootloader and proceeds."""
        deadline = time.time() + max(wait_s, 0.0)
        prompted = False
        attempt = 0
        while True:
            attempt += 1
            self.ser.reset_input_buffer()
            self._w(INIT)
            b = self._r(1, timeout=0.7)
            if b:
                if b[0] == ACK:
                    log(f"[sync] ROM bootloader responded ACK (attempt {attempt})")
                    return True
                if b[0] == NACK:
                    # 0x1F here means the bootloader is already initialized — that's fine.
                    log(f"[sync] ROM already initialized (NACK on 0x7F, attempt {attempt})")
                    return True
                if self.verbose:
                    log(f"    <- 0x{b[0]:02X} (not ACK/NACK), retrying")
            elif self.verbose:
                log(f"    <- (silence, attempt {attempt})")
            # stop once both the time budget is spent AND we've tried a few times
            if time.time() >= deadline and attempt >= 6:
                return False
            if wait_s >= 2.0 and not prompted:
                log(f"[sync] >>> press BOOT0 + RST on the board now (waiting up to {int(wait_s)}s) <<<")
                prompted = True
            time.sleep(0.1)

    def get(self):
        self._cmd(0x00)
        n = self._r(1)
        if not n:
            raise BLError("Get: no length byte")
        count = n[0]                       # N = number of following bytes - 1
        payload = self._r(count + 1)
        self._ack(what="Get end")
        ver = payload[0]
        cmds = list(payload[1:])
        log(f"[get] bootloader version 0x{ver:02X}, {len(cmds)} commands: "
            + " ".join(f"{c:02X}" for c in cmds))
        return ver, cmds

    def get_id(self):
        self._cmd(0x02)
        n = self._r(1)
        if not n:
            raise BLError("Get-ID: no length byte")
        data = self._r(n[0] + 1)
        self._ack(what="Get-ID end")
        pid = (data[0] << 8) | data[1] if len(data) >= 2 else data[0]
        log(f"[get-id] product ID = 0x{pid:03X}"
            + ("  (STM32H743 OK)" if pid == H743_PID else f"  (expected 0x{H743_PID:03X}!)"))
        return pid

    def mass_erase(self):
        """Extended Erase (0x44): mass-erase via the 0xFFFF special code."""
        log("[erase] extended mass erase...")
        self._cmd(0x44, timeout=2.0)
        frame = bytes([0xFF, 0xFF])
        self._w(frame + bytes([self._xor(frame)]))
        self._ack(timeout=40.0, what="mass erase")   # H7 mass erase can take many seconds
        log("[erase] done")

    def write_memory(self, addr, data):
        # address frame: 4 bytes MSB-first + XOR checksum
        a = bytes([(addr >> 24) & 0xFF, (addr >> 16) & 0xFF, (addr >> 8) & 0xFF, addr & 0xFF])
        self._cmd(0x31)
        self._w(a + bytes([self._xor(a)]))
        self._ack(what="WriteMem addr")
        # data frame: (N-1) + data + XOR(N-1, data...)
        n_minus_1 = len(data) - 1
        chk = self._xor(bytes([n_minus_1]) + data)
        self._w(bytes([n_minus_1]) + data + bytes([chk]))
        self._ack(timeout=4.0, what="WriteMem data")

    def read_memory(self, addr, length):
        """Read Memory (0x11): up to 256 bytes from any CPU-addressable location."""
        assert 1 <= length <= 256
        self._cmd(0x11)
        a = bytes([(addr >> 24) & 0xFF, (addr >> 16) & 0xFF, (addr >> 8) & 0xFF, addr & 0xFF])
        self._w(a + bytes([self._xor(a)]))
        self._ack(what="ReadMem addr")
        n = length - 1
        self._w(bytes([n, n ^ 0xFF]))
        self._ack(what="ReadMem N")
        data = self._r(length, timeout=3.0)
        if len(data) != length:
            raise BLError(f"ReadMem short read: got {len(data)}/{length}")
        return data

    def read_u32(self, addr):
        d = self.read_memory(addr, 4)
        return d[0] | (d[1] << 8) | (d[2] << 16) | (d[3] << 24)  # little-endian

    def go(self, addr):
        log(f"[go] jumping to 0x{addr:08X}")
        self._cmd(0x21)
        a = bytes([(addr >> 24) & 0xFF, (addr >> 16) & 0xFF, (addr >> 8) & 0xFF, addr & 0xFF])
        self._w(a + bytes([self._xor(a)]))
        self._ack(what="Go addr")

    def send_console_line(self, line):
        """Send a plain console command (e.g. 'dfu') before the bootloader takes over."""
        log(f"[console] sending {line!r} to trigger programmatic bootloader entry")
        self._w((line + "\n").encode("ascii"))
        self.ser.flush()


def flash_image(bl, path, do_go):
    with open(path, "rb") as f:
        image = f.read()
    if len(image) % FLASHWORD:
        pad = FLASHWORD - (len(image) % FLASHWORD)
        image += b"\xFF" * pad
        log(f"[image] padded to {len(image)} bytes (multiple of {FLASHWORD})")
    log(f"[image] {len(image)} bytes -> 0x{FLASH_BASE:08X}")

    bl.mass_erase()

    total = len(image)
    written = 0
    t0 = time.time()
    while written < total:
        chunk = image[written:written + WRITE_CHUNK]
        bl.write_memory(FLASH_BASE + written, chunk)
        written += len(chunk)
        pct = 100 * written // total
        sys.stdout.write(f"\r[write] {written}/{total} bytes ({pct}%)")
        sys.stdout.flush()
    dt = time.time() - t0
    log(f"\n[write] complete in {dt:.1f}s")

    if do_go:
        bl.go(FLASH_BASE)


def main():
    ap = argparse.ArgumentParser(description="DTR-safe STM32 USART bootloader flasher (AN3155)")
    ap.add_argument("--port", required=True, help="serial port, e.g. COM16")
    ap.add_argument("--baud", type=int, default=115200)
    ap.add_argument("--bin", help="firmware .bin to flash (omit with --probe)")
    ap.add_argument("--enter", choices=["manual", "dfu"], default="manual",
                    help="manual: you entered the bootloader by hand (BOOT0+RST). "
                         "dfu: send 'dfu' console line to jump programmatically.")
    ap.add_argument("--settle", type=float, default=1.5,
                    help="seconds to wait after opening the port for the S3 bridge to reboot")
    ap.add_argument("--probe", action="store_true",
                    help="only sync + Get + Get-ID (no erase/write) — proves the bootloader is reachable")
    ap.add_argument("--read-ob", action="store_true",
                    help="read + decode the FLASH option-byte registers via Read Memory (no write)")
    ap.add_argument("--read", metavar="ADDR:LEN", action="append", default=[],
                    help="hexdump LEN bytes from ADDR (e.g. 0x08100000:32), no write. Repeatable.")
    ap.add_argument("--go", action="store_true", help="issue Go to 0x08000000 after flashing")
    ap.add_argument("--wait", type=float, default=0.0,
                    help="poll for the bootloader up to N seconds — launch the command, THEN press "
                         "BOOT0+RST on the board (the middle-ground hands-free-ish flow)")
    ap.add_argument("-v", "--verbose", action="store_true")
    args = ap.parse_args()

    if not args.probe and not args.read_ob and not args.read and not args.bin:
        ap.error("--bin is required unless --probe / --read-ob / --read is given")

    bl = STBootloader(args.port, args.baud, verbose=args.verbose)
    try:
        bl.open(settle_s=args.settle)

        if args.enter == "dfu":
            bl.send_console_line("dfu")
            # Firmware programs BOOT_ADD0 -> system memory then resets; the boot ROM re-enters via the
            # hardware path. Give the option-byte write + reset time to land (sync() also retries).
            log("[enter] waiting 1.5s for the option-byte reset to re-enter the ROM bootloader...")
            time.sleep(1.5)

        if not bl.sync(wait_s=args.wait):
            log("")
            log("[FAIL] no response to 0x7F autobaud.")
            log("  - If --enter manual: is the board actually in the ROM bootloader (BOOT0 high at reset)?")
            log("  - If --enter dfu: the programmatic jump likely did not land (the suspect we're testing).")
            log("  - Either way, if Get/probe never ACKs, the bridge path or entry is the problem.")
            return 2

        bl.get()
        bl.get_id()

        if args.probe:
            log("[probe] bootloader reachable and identified — the bridge + protocol path WORKS.")
            return 0

        if args.read_ob:
            FLASH = 0x52002000
            optsr_cur = bl.read_u32(FLASH + 0x1C)
            optsr_prg = bl.read_u32(FLASH + 0x20)
            wpsn_cur  = bl.read_u32(FLASH + 0x38)
            boot_cur  = bl.read_u32(FLASH + 0x40)
            boot_prg  = bl.read_u32(FLASH + 0x44)

            def rdp(v):
                b = (v >> 8) & 0xFF
                return {0xAA: "L0", 0xCC: "L2"}.get(b, "L1") + f" (0x{b:02X})"

            def add0(v):
                return (v & 0xFFFF) << 16

            def add1(v):
                return ((v >> 16) & 0xFFFF) << 16

            log("")
            log(f"[ob] OPTSR_CUR = 0x{optsr_cur:08X}   RDP={rdp(optsr_cur)}  BOR_LEV={(optsr_cur >> 2) & 3}"
                f"  IWDG_SW={(optsr_cur >> 4) & 1}")
            log(f"[ob] OPTSR_PRG = 0x{optsr_prg:08X}")
            log(f"[ob] WPSN_CUR1 = 0x{wpsn_cur:08X}   (sector write-protect; 0xFF = none protected)")
            log(f"[ob] BOOT_CUR  : ADD0=0x{add0(boot_cur):08X}  ADD1=0x{add1(boot_cur):08X}")
            log(f"[ob] BOOT_PRG  : ADD0=0x{add0(boot_prg):08X}  ADD1=0x{add1(boot_prg):08X}")
            log("[ob] healthy defaults: RDP=L0(0xAA)  BOOT_ADD0=0x08000000  BOOT_ADD1=0x1FF00000  WPSN=0x...FFF")
            return 0

        if args.read:
            for spec in args.read:
                astr, _, lstr = spec.partition(":")
                addr = int(astr, 0)
                length = int(lstr, 0) if lstr else 32
                data = bl.read_memory(addr, length)
                hexs = " ".join(f"{b:02X}" for b in data)
                log(f"[read] 0x{addr:08X} ({length}B): {hexs}")
            return 0

        flash_image(bl, args.bin, args.go)
        log("[done] flash successful.")
        return 0

    except BLError as e:
        log(f"\n[FAIL] {e}")
        return 1
    except serial.SerialException as e:
        log(f"\n[FAIL] serial: {e}")
        return 1
    finally:
        bl.close()


if __name__ == "__main__":
    sys.exit(main())
