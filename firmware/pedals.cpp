#include "pedals.h"

#include "mapping.h" // PEDAL_TX / PEDAL_RX

// State definitions
enum PedalsState : uint8_t {
  STATE_INIT = 0,
  STATE_STEP0,    // 250000 baud, wait for 0x0A, send 0x1A
  STATE_STEP1,    // 250000 baud, wait for 0x05, send 0x15
  STATE_STEP2,    // 115200 baud, wait for 36-byte query, send 36-byte response
  STATE_STREAMING // 115200 baud, continuously streaming pedal packets
};

// State variables
static PedalsState g_state = STATE_INIT;
static uint32_t g_lastActivityTime = 0;
static uint32_t g_lastStreamTime = 0;

// Warmup window. On boot we silently drain Serial1 for ~2 seconds before
// engaging the handshake state machine. The wheelbase often still has
// state from the previous adapter session — partway through its own retry
// loop, or with stale TX buffers — and any half-finished 0x0A / 0x05 we'd
// act on lands us in an out-of-sync STEP1 that the wheelbase won't follow
// through. Giving the line ~2 s of silence lets the wheelbase's retry
// timer fire fresh and re-initiate cleanly.
//
// 0 once warmup has completed (set by pedalsUpdate).
static uint32_t g_warmupUntil = 0;
constexpr uint32_t WARMUP_MS = 2000;

// Step 2 handshake — lenient any-order packet collector.
//
// The DD+ wheelbase sends three 12-byte framed query packets (cmd 0x00,
// 0x02, 0x03) at 115200 baud, but in arbitrary order and repeatedly until
// it gets a response. Observed empirically on a real DD+: the wheelbase
// emits cmd 0x00 and cmd 0x03 several times each before finally sending
// cmd 0x02, and the 0x02 payload differs from the proxy.go reference
// (0x00 0xFF vs. 0xFF 0x00 — likely a firmware revision difference; CRC
// always validates for the packet as sent). Strict linear matching
// against the proxy.go expected sequence fails on this base.
//
// We collect 12-byte framed packets (0x7B…0x7D), tally which command IDs
// have arrived, and fire the identity response once 0x03 has been seen.
static uint8_t g_step2Buf[12];
static uint8_t g_step2BufIdx = 0;
static bool g_step2Got00 = false;
static bool g_step2Got02 = false;
static bool g_step2Got03 = false;

// Step 2 response packets — sent INDIVIDUALLY per matching query, not bulk.
// Per the community sketch in GeekyDeaks/fanatec-pedal-emulator#4, the
// wheelbase expects an interactive ack per command packet. Sending all 36
// bytes only after collecting all 3 queries (as proxy.go does) misses a
// tight per-packet timeout on at least some firmware revisions.
//
// Response → query mapping (same bytes as proxy.go's bulk STEP2_TX):
//   cmd 0x02 (config query)  → STEP2_TX_CMD_02
//   cmd 0x00 (ping/null)     → STEP2_TX_CMD_00
//   cmd 0x03 (version query) → STEP2_TX_CMD_03
static const uint8_t STEP2_TX_CMD_02[12] = {0x7B, 0x05, 0x06, 0x62, 0x00, 0x00,
                                            0x00, 0x00, 0x00, 0x00, 0x6D, 0x7D};
static const uint8_t STEP2_TX_CMD_00[12] = {0x7B, 0x07, 0x0B, 0x00, 0x00, 0x00,
                                            0x00, 0x00, 0x00, 0x00, 0x12, 0x7D};
static const uint8_t STEP2_TX_CMD_03[12] = {0x7B, 0x08, 0x01, 0x06, 0x07, 0x00,
                                            0x00, 0x00, 0x00, 0x00, 0xBF, 0x7D};

// Pedal position values (0..65535)
static uint16_t g_throttle = 0;
static uint16_t g_brake = 0;
static uint16_t g_clutch = 0;
static uint16_t g_handbrake = 0;

// CRC-8 Dallas/Maxim table (polynomial 0x8C)
static uint8_t g_crcTable[256];

static void initCrcTable() {
  for (int i = 0; i < 256; i++) {
    uint8_t crc = i;
    for (int j = 0; j < 8; j++) {
      bool bit = (crc & 0x01) != 0;
      crc >>= 1;
      if (bit) {
        crc ^= 0x8C;
      }
    }
    g_crcTable[i] = crc;
  }
}

static uint8_t generateCrc(const uint8_t* data, size_t len) {
  uint8_t crc = 0xFF;
  for (size_t i = 0; i < len; i++) {
    crc = g_crcTable[data[i] ^ crc];
  }
  return crc;
}

// Re-initialise Serial1 at a new baud rate. On ESP32-S3 Arduino we have to
// re-supply the pin assignments because Serial.end() drops the peripheral
// config. updateBaudRate() would be lighter but doesn't reset the FIFO,
// which is the whole point of the call site (STEP1 → STEP2 transition
// needs a clean slate).
static void reinitSerial(uint32_t baud) {
  Serial1.end();
  delay(10); // give serial peripheral time to settle
  Serial1.begin(baud, SERIAL_8N1, PEDAL_RX, PEDAL_TX);
}

static void resetStep2State() {
  g_step2BufIdx = 0;
  g_step2Got00 = false;
  g_step2Got02 = false;
  g_step2Got03 = false;
}

static void resetToStep0() {
  Serial.println("[Pedal] Transitioning to STATE_STEP0 (250000 baud)...");
  reinitSerial(250000);
  g_state = STATE_STEP0;
  resetStep2State();
  g_lastActivityTime = millis();
}

void pedalsInit() {
  initCrcTable();
  g_warmupUntil = millis() + WARMUP_MS;
  Serial.print("[Pedal] Warmup: draining Serial1 for ");
  Serial.print(WARMUP_MS);
  Serial.println(" ms before engaging handshake.");
  resetToStep0();
}

void pedalsForceReset() {
  // Manual reset (from the JSON `reset_pedals` command). Skip the warmup
  // — the user explicitly asked us to re-arm right now, not in 2 s.
  Serial.println("[Pedal] Manual reset requested.");
  g_warmupUntil = 0;
  resetToStep0();
}

static void sendPedalPacket() {
  uint8_t packet[12];

  packet[0] = 0x7B; // Start frame marker
  packet[1] = 0x01; // Send pedals command

  // Throttle (Little Endian)
  packet[2] = g_throttle & 0xFF;
  packet[3] = (g_throttle >> 8) & 0xFF;

  // Brake (Little Endian)
  packet[4] = g_brake & 0xFF;
  packet[5] = (g_brake >> 8) & 0xFF;

  // Clutch (Little Endian)
  packet[6] = g_clutch & 0xFF;
  packet[7] = (g_clutch >> 8) & 0xFF;

  // Handbrake (Little Endian)
  packet[8] = g_handbrake & 0xFF;
  packet[9] = (g_handbrake >> 8) & 0xFF;

  // Generate CRC over packet[1] through packet[9] (9 bytes total)
  packet[10] = generateCrc(&packet[1], 9);

  packet[11] = 0x7D; // End frame marker

  Serial1.write(packet, 12);
}

void pedalsUpdate() {
  // Warmup: silently drop any bytes the wheelbase is still sending from
  // a stale prior session. Once the window closes the line should be
  // quiet and the wheelbase's next 0x0A retry catches us in a clean
  // STEP0. See WARMUP_MS comment up top for rationale.
  if (g_warmupUntil != 0) {
    if (millis() < g_warmupUntil) {
      while (Serial1.available() > 0)
        Serial1.read();
      return;
    }
    Serial.println("[Pedal] Warmup complete — engaging handshake state machine.");
    g_warmupUntil = 0;
    g_lastActivityTime = millis(); // fresh baseline for the STEP1/2 timeout
  }

  // Check for handshake timeouts. STEP0 is exempt because we wait
  // indefinitely there for the wheelbase to initiate. STREAMING is
  // exempt because steady streaming has no expected return traffic
  // — the wheelbase keeps sending stale 0x7B-framed queries even
  // after STEP2 completes (documented quirk; we ignore them).
  if (g_state != STATE_STEP0 && g_state != STATE_STREAMING) {
    if (millis() - g_lastActivityTime > 2000) {
      Serial.println("[Pedal] Handshake timeout. Restarting from Step 0.");
      resetToStep0();
      return;
    }
  }

  // Handle incoming data based on state
  while (Serial1.available() > 0) {
    uint8_t b = Serial1.read();
    g_lastActivityTime = millis();

    switch (g_state) {
      case STATE_STEP0:
        if (b == 0x0A) {
          Serial.println("[Pedal] Step 0: Received 0x0A. Sending 0x1A.");
          Serial1.write(0x1A);
          g_state = STATE_STEP1;
        }
        break;

      case STATE_STEP1:
        if (b == 0x05) {
          Serial.println("[Pedal] Step 1: Received 0x05. Sending 0x15.");
          Serial1.write(0x15);
          Serial1.flush(); // wait for byte to fully transmit before baud change

          Serial.println("[Pedal] Switching to 115200 baud (STATE_STEP2)...");
          reinitSerial(115200);
          resetStep2State();
          g_state = STATE_STEP2;
        } else {
          Serial.print("[Pedal] Step 1 error: expected 0x05, got 0x");
          Serial.print(b, HEX);
          Serial.println(". Resetting to Step 0.");
          resetToStep0();
        }
        break;

      case STATE_STEP2: {
        // Wait for start-of-frame marker before accumulating.
        if (g_step2BufIdx == 0) {
          if (b == 0x7B) {
            g_step2Buf[0] = b;
            g_step2BufIdx = 1;
          }
          // Silently ignore anything before the next 0x7B. Baud-rate
          // drift garbage gets dropped here without spamming Serial.
          break;
        }

        g_step2Buf[g_step2BufIdx++] = b;
        if (g_step2BufIdx < 12)
          break;

        // Got a full 12-byte packet — validate framing + CRC, tag command.
        g_step2BufIdx = 0;
        if (g_step2Buf[11] != 0x7D) {
          Serial.println("[Pedal] Step 2: bad end marker, skipping packet.");
          break;
        }
        const uint8_t rxCrc = g_step2Buf[10];
        const uint8_t calcCrc = generateCrc(&g_step2Buf[1], 9);
        if (rxCrc != calcCrc) {
          Serial.print("[Pedal] Step 2: CRC fail (got 0x");
          if (rxCrc < 0x10)
            Serial.print('0');
          Serial.print(rxCrc, HEX);
          Serial.print(" expected 0x");
          if (calcCrc < 0x10)
            Serial.print('0');
          Serial.print(calcCrc, HEX);
          Serial.println("), skipping packet.");
          break;
        }

        const uint8_t cmd = g_step2Buf[1];
        switch (cmd) {
          case 0x00:
            if (!g_step2Got00) {
              Serial.println("[Pedal] Step 2: ack cmd 0x00");
              Serial1.write(STEP2_TX_CMD_00, sizeof(STEP2_TX_CMD_00));
              g_step2Got00 = true;
            }
            break;
          case 0x02:
            if (!g_step2Got02) {
              Serial.println("[Pedal] Step 2: ack cmd 0x02");
              Serial1.write(STEP2_TX_CMD_02, sizeof(STEP2_TX_CMD_02));
              g_step2Got02 = true;
            }
            break;
          case 0x03:
            if (!g_step2Got03) {
              Serial.println("[Pedal] Step 2: ack cmd 0x03");
              Serial1.write(STEP2_TX_CMD_03, sizeof(STEP2_TX_CMD_03));
              g_step2Got03 = true;
            }
            break;
          default:
            Serial.print("[Pedal] Step 2: unexpected cmd 0x");
            if (cmd < 0x10)
              Serial.print('0');
            Serial.println(cmd, HEX);
            break;
        }

        // The 0x03 ack is the version-identity packet (its payload tells the
        // wheelbase we're CS Pedals V3). Once that's acked, the wheelbase
        // expects streaming data immediately — if we don't send any, the
        // app drops the pedals after a few seconds. The 0x00 and 0x02
        // queries on our DD+ firmware arrive less reliably (sometimes never
        // before we'd transition), so we don't gate on them. Matches the
        // sketch in GeekyDeaks/fanatec-pedal-emulator#4.
        if (g_step2Got03) {
          Serial1.flush();
          Serial.println("[Pedal] Handshake complete (cmd 0x03 acked). → STREAMING.");
          g_state = STATE_STREAMING;
          g_lastStreamTime = millis();
        }
        break;
      }

      case STATE_STREAMING:
        // The wheelbase keeps emitting 12-byte 0x7B-framed query packets
        // (cmd 0x00 / 0x02 / 0x03) even after STEP2 completes — observed
        // on real DD+ firmware. They're ignored: re-acking them isn't
        // needed and bouncing back to STEP0 on every one would flap the
        // handshake. If the wheelbase truly wants to re-handshake, the
        // stream stops being received and the wheelbase drops pedals;
        // the user can then hit "Re-arm pedals handshake" in the UI.
        break;

      default:
        break;
    }
  }

  // Handle streaming packets (every 10 ms -> 100 Hz frequency)
  if (g_state == STATE_STREAMING) {
    uint32_t now = millis();
    if (now - g_lastStreamTime >= 10) {
      g_lastStreamTime = now;
      sendPedalPacket();
    }
  }
}

void setPedalThrottle(uint16_t val) { g_throttle = val; }
void setPedalBrake(uint16_t val) { g_brake = val; }
void setPedalClutch(uint16_t val) { g_clutch = val; }
void setPedalHandbrake(uint16_t val) { g_handbrake = val; }

uint16_t getPedalThrottle() { return g_throttle; }
uint16_t getPedalBrake() { return g_brake; }
uint16_t getPedalClutch() { return g_clutch; }
uint16_t getPedalHandbrake() { return g_handbrake; }

const char* getPedalsStateName() {
  switch (g_state) {
    case STATE_INIT:
      return "INIT";
    case STATE_STEP0: // Fallthrough
    case STATE_STEP1:
      return "HANDSHAKE_250K";
    case STATE_STEP2:
      return "HANDSHAKE_115K";
    case STATE_STREAMING:
      return "STREAMING_115K";
    default:
      return "UNKNOWN";
  }
}
