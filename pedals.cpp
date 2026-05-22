#include "pedals.h"

// State definitions
enum PedalsState : uint8_t {
  STATE_INIT = 0,
  STATE_STEP0,       // 250000 baud, wait for 0x0A, send 0x1A
  STATE_STEP1,       // 250000 baud, wait for 0x05, send 0x15
  STATE_STEP2,       // 115200 baud, wait for 36-byte query, send 36-byte response
  STATE_STREAMING    // 115200 baud, continuously streaming pedal packets
};

// State variables
static PedalsState g_state = STATE_INIT;
static uint32_t g_lastActivityTime = 0;
static uint32_t g_lastStreamTime = 0;

// Step 2 query sequence (36 bytes total: 3 x 12-byte packets)
static const uint8_t STEP2_RX[] = {
  0x7B, 0x02, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x26, 0x7D,
  0x7B, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xAA, 0x7D,
  0x7B, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x5F, 0x7D
};
static uint8_t g_step2Index = 0;

// Step 2 response sequence (36 bytes total: 3 x 12-byte packets)
static const uint8_t STEP2_TX[] = {
  0x7B, 0x05, 0x06, 0x62, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x6D, 0x7D,
  0x7B, 0x07, 0x0B, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x12, 0x7D,
  0x7B, 0x08, 0x01, 0x06, 0x07, 0x00, 0x00, 0x00, 0x00, 0x00, 0xBF, 0x7D
};

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

static void reinitSerial(uint32_t baud) {
  Serial3.end();
  delay(10); // give serial peripheral time to settle
  Serial3.begin(baud);
}

static void resetToStep0() {
  Serial.println("[Pedal] Transitioning to STATE_STEP0 (250000 baud)...");
  reinitSerial(250000);
  g_state = STATE_STEP0;
  g_step2Index = 0;
  g_lastActivityTime = millis();
}

void pedalsInit() {
  initCrcTable();
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
  
  Serial3.write(packet, 12);
}

void pedalsUpdate() {
  // Check for handshake timeouts
  if (g_state != STATE_STEP0 && g_state != STATE_STREAMING) {
    if (millis() - g_lastActivityTime > 2000) {
      Serial.println("[Pedal] Handshake timeout. Restarting from Step 0.");
      resetToStep0();
      return;
    }
  }

  // Handle incoming data based on state
  while (Serial3.available() > 0) {
    uint8_t b = Serial3.read();
    g_lastActivityTime = millis();

    switch (g_state) {
      case STATE_STEP0:
        if (b == 0x0A) {
          Serial.println("[Pedal] Step 0: Received 0x0A. Sending 0x1A.");
          Serial3.write(0x1A);
          g_state = STATE_STEP1;
        }
        break;

      case STATE_STEP1:
        if (b == 0x05) {
          Serial.println("[Pedal] Step 1: Received 0x05. Sending 0x15.");
          Serial3.write(0x15);
          Serial3.flush(); // wait for byte to fully transmit before baud change
          
          Serial.println("[Pedal] Switching to 115200 baud (STATE_STEP2)...");
          reinitSerial(115200);
          g_step2Index = 0;
          g_state = STATE_STEP2;
        } else {
          Serial.print("[Pedal] Step 1 error: expected 0x05, got 0x");
          Serial.print(b, HEX);
          Serial.println(". Resetting to Step 0.");
          resetToStep0();
        }
        break;

      case STATE_STEP2:
        if (b == STEP2_RX[g_step2Index]) {
          g_step2Index++;
          if (g_step2Index == 36) {
            Serial.println("[Pedal] Step 2: Handshake queries match. Sending 36-byte identity response.");
            Serial3.write(STEP2_TX, 36);
            Serial3.flush();
            
            Serial.println("[Pedal] Handshake complete! Transitioning to STATE_STREAMING.");
            g_state = STATE_STREAMING;
            g_lastStreamTime = millis();
          }
        } else {
          Serial.print("[Pedal] Step 2 mismatch at index ");
          Serial.print(g_step2Index);
          Serial.print(": expected 0x");
          Serial.print(STEP2_RX[g_step2Index], HEX);
          Serial.print(", got 0x");
          Serial.print(b, HEX);
          Serial.println(". Resetting to Step 0.");
          resetToStep0();
        }
        break;

      case STATE_STREAMING:
        // In streaming mode, we don't expect any unsolicited commands.
        // If the wheelbase suddenly sends 0x0A at 250000 baud, it would trigger a mismatch or timeout.
        // If it sends anything here, we can ignore it or log it for diagnostics.
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

void setPedalThrottle(uint16_t val)   { g_throttle = val; }
void setPedalBrake(uint16_t val)      { g_brake = val; }
void setPedalClutch(uint16_t val)     { g_clutch = val; }
void setPedalHandbrake(uint16_t val)  { g_handbrake = val; }

uint16_t getPedalThrottle()   { return g_throttle; }
uint16_t getPedalBrake()      { return g_brake; }
uint16_t getPedalClutch()     { return g_clutch; }
uint16_t getPedalHandbrake()  { return g_handbrake; }

const char* getPedalsStateName() {
  switch (g_state) {
    case STATE_INIT:      return "INIT";
    case STATE_STEP0:     // Fallthrough
    case STATE_STEP1:     return "HANDSHAKE_250K";
    case STATE_STEP2:     return "HANDSHAKE_115K";
    case STATE_STREAMING: return "STREAMING_115K";
    default:              return "UNKNOWN";
  }
}
