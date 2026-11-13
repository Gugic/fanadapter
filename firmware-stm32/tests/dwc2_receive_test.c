// Register model for the actual TinyUSB receive/completion handlers extracted by the Python test.
// No USB peripheral or serial port is opened. The CDC consumer copies the completion byte count.
#include <assert.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "json_min.h"

typedef struct { uint8_t ep_ch_num; uint16_t byte_count; unsigned packet_status; } dwc2_grxstsp_t;
typedef struct { uint32_t doeptsiz; struct { unsigned xfer_size; } tsiz_bm; } dwc2_dep_t;
typedef struct {
  volatile uint32_t fifo[2][1]; dwc2_grxstsp_t grxstsp_bm; dwc2_dep_t epout[2];
} dwc2_regs_t;
typedef struct { void *ff; uint8_t *buffer; uint16_t total_len, max_size; } xfer_ctl_t;
typedef struct { bool setup_phase_done, xfer_complete, status_phase_rx, setup_packet_rx; } dwc2_doepint_t;
static dwc2_regs_t regs;
static xfer_ctl_t transfer;
static struct { uint32_t setup_packet[2]; } _dcd_usbbuf;
static struct { uint16_t ep0_pending[2]; } _dcd_data;
static uint8_t ep_buffer[64], received[1024];
static const uint8_t *packet;
static size_t received_len;
static unsigned reported;
#define DWC2_REG(r) ((void)(r), &regs)
#define XFER_CTL_BASE(ep, dir) ((void)(ep), (void)(dir), &transfer)
#define TUSB_DIR_OUT 0
#define DOEPTSIZ_STUPCNT_Pos 0
#define XFER_RESULT_SUCCESS 0
enum { GRXSTS_PKTSTS_GLOBAL_OUT_NAK, GRXSTS_PKTSTS_SETUP_RX, GRXSTS_PKTSTS_SETUP_DONE,
       GRXSTS_PKTSTS_RX_DATA, GRXSTS_PKTSTS_RX_COMPLETE };

static void dfifo_read_packet(dwc2_regs_t *r, uint8_t *dst, uint16_t len) {
  (void)r; memcpy(dst, packet, len);
}
static void tu_fifo_write_n_const_addr_full_words(void *f, const void *reg, uint16_t n) {
  (void)f; (void)reg; (void)n; abort(); // CDC uses an endpoint buffer, not this FIFO path.
}
static void dcd_event_setup_received(uint8_t r, const void *p, bool isr) {
  (void)r; (void)p; (void)isr; abort();
}
static void edpt_schedule_packets(uint8_t r, uint8_t ep, uint8_t dir) {
  (void)r; (void)ep; (void)dir; abort();
}
static void dcd_event_xfer_complete(uint8_t r, uint8_t ep, uint16_t n, int result, bool isr) {
  (void)r; (void)ep; (void)result; (void)isr;
  reported = n;
  assert(n <= sizeof(ep_buffer) && received_len + n <= sizeof(received));
  // cdcd_xfer_cb copies xferred_bytes from its endpoint buffer into the CDC RX FIFO.
  memcpy(received + received_len, ep_buffer, n);
  received_len += n;
}

#include "dwc2_receive_handlers.inc"

static void receive_packet(const uint8_t *data, unsigned n, unsigned ep) {
  assert(n <= sizeof(ep_buffer));
  packet = data;
  transfer = (xfer_ctl_t){ .buffer = ep_buffer, .total_len = 64, .max_size = 64 };
  if (ep == 0) { transfer.total_len += 64; _dcd_data.ep0_pending[0] = 64; }
  regs.grxstsp_bm = (dwc2_grxstsp_t){ .ep_ch_num = ep, .byte_count = n,
                                   .packet_status = GRXSTS_PKTSTS_RX_DATA };
  regs.epout[ep].tsiz_bm.xfer_size = 64 - n;
  handle_rxflvl_irq(0);
  handle_epout_slave(0, ep, (dwc2_doepint_t){ .xfer_complete = true });
  if (reported != n) {
    fprintf(stderr, "OUT packet of %u bytes incorrectly reported %u bytes\n", n, reported);
    exit(1);
  }
  assert(ep != 0 || _dcd_data.ep0_pending[0] == 0);
}

int main(void) {
  uint8_t command[206];
  const char prefix[] = "{\"cmd\":\"version\",\"pad\":\"";
  memset(command, 'x', sizeof(command));
  memcpy(command, prefix, sizeof(prefix) - 1);
  memcpy(command + sizeof(command) - 3, "\"}\n", 3);
  memset(ep_buffer, 0xa5, sizeof(ep_buffer));
  receive_packet(NULL, 0, 1); // Initial ZLP must not deliver uninitialized endpoint contents.
  for (unsigned n = 1; n <= 64; n++) {
    received_len = 0;
    receive_packet(command, n, 1);
    receive_packet(NULL, 0, 1); // ZLP after a full or short transfer must not duplicate it.
    assert(received_len == n && memcmp(received, command, n) == 0);
  }
  received_len = 0;
  for (unsigned off = 0; off < sizeof(command); off += 64) {
    unsigned n = sizeof(command) - off;
    if (n > 64) n = 64;
    receive_packet(command + off, n, 1);
    if (n == 64) receive_packet(NULL, 0, 1);
  }
  assert(received_len == sizeof(command) && memcmp(received, command, sizeof(command)) == 0);
  json_kv fields[4];
  assert(json_parse_object((const char *)received, received_len, fields, 4) == 2);
  received_len = 0;
  receive_packet(command, 14, 0); // Preserve EP0's remaining-packet accounting too.
  receive_packet(NULL, 0, 0);
  assert(received_len == 14);
  puts("DWC2 receive regression: zero, short, full, fragmented and EP0 packets passed");
  return 0;
}
