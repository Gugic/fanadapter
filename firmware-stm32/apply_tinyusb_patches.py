# Applies fanadapter's TinyUSB patches to the PlatformIO-fetched checkout under .pio/libdeps.
#
# Why patch at build time: TinyUSB is pulled from upstream git (pinned by SHA in platformio.ini),
# and `pio pkg update` or a wiped .pio/ regenerates it — an in-place edit would silently vanish.
# This script runs as `extra_scripts = pre:` on every build: it verifies the complete replacement
# blocks and applies them if absent. Partial patches and upstream drift fail the build. Both
# drivers are validated before either is written, so a failed check cannot leave a half-patched pair.
#
# Device receive fix: backport TinyUSB PR #3293's dcd_dwc2.c change. A zero-length OUT packet
# must reduce the reported transfer length to zero; otherwise CDC replays the previous endpoint
# buffer into its RX FIFO. This corrupts fragmented JSON or executes a 64-byte command twice.
# Only the driver fix is carried here; TinyUSB remains pinned to 0.18.0.
#
# Driver hardening (full investigation and verdict in PORT-STATUS.md):
# In hcd_dwc2.c, channel_send_in_token() asserts on the 8-deep periodic request queue; in a
# release build a full queue makes it silently return false. channel_xfer_start() ignored that and
# returned true unconditionally, and edpt_xfer_kickoff() believed it — leaving the channel
# allocated but never enabled: no IN token ever reaches the bus, no interrupt fires, the endpoint
# could remain busy. The patch propagates failure and deallocates so callers can retry. Other
# patches avoid dropping disable operations and preserve interrupt masking during allocation.
# These were real latent bugs, but did NOT cause the observed pedal freeze: usb_stall reproduced
# the pedal's own firmware hanging after a long polling gap, with no request-queue failures.
# A stale CHENA=0 register alone was not evidence of an allocated channel. Keep that distinction
# when interpreting the counters and allocation mask added here.

import os

MARKER = "FANADAPTER PATCH"

# (old, new, expected occurrence count when unpatched)
# NOTE: channel_send_in_token has SIX call sites; only the two inside channel_xfer_start are
# patched (anchored by their distinct else-branches). The other four are mid-transfer ISR retry
# continuations (split/NAK paths) where a bool return has nowhere to go — out of scope.
GUARDED_CALL = ("      if (!channel_send_in_token(dwc2, channel)) return false; "
                "// FANADAPTER PATCH: propagate token-post failure\n")
REPLACEMENTS = [
    (
        # Diagnostic counters + allocation visibility, inserted ahead of channel_disable. The
        # counters replace the silent TU_ASSERT returns below so usb_status can show WHICH
        # queue-full path fired (the discriminating evidence the freeze investigation needs);
        # the alloc mask lets usb_status distinguish an allocated-but-never-enabled channel
        # (wedge) from a deallocated one whose HCCHAR still holds stale contents (ghost).
        "TU_ATTR_ALWAYS_INLINE static inline bool channel_disable(",
        "// FANADAPTER PATCH: freeze diagnostics — see firmware usb_status\n"
        "uint32_t tusb_fanadapter_reqq_fail_token;\n"
        "uint32_t tusb_fanadapter_reqq_fail_disable;\n"
        "uint32_t tusb_fanadapter_alloc_mask(void) {\n"
        "  uint32_t m = 0;\n"
        "  for (uint8_t i = 0; i < 16; i++) {\n"
        "    if (_hcd_data.xfer[i].allocated) m |= (1u << i);\n"
        "  }\n"
        "  return m;\n"
        "}\n"
        "TU_ATTR_ALWAYS_INLINE static inline bool channel_disable(",
        1,
    ),
    (
        # channel_disable: count + fail visibly instead of TU_ASSERT's silent return
        "  // disable also require request queue\n"
        "  TU_ASSERT(req_queue_avail(dwc2, edpt_is_periodic(channel->hcchar_bm.ep_type)));\n",
        "  // disable also require request queue\n"
        "  // FANADAPTER PATCH: count queue-full disable failures\n"
        "  if (!req_queue_avail(dwc2, edpt_is_periodic(channel->hcchar_bm.ep_type))) {\n"
        "    tusb_fanadapter_reqq_fail_disable++;\n"
        "    return false;\n"
        "  }\n",
        1,
    ),
    (
        # channel_send_in_token: count + fail visibly instead of TU_ASSERT's silent return
        "TU_ATTR_ALWAYS_INLINE static inline bool channel_send_in_token(const dwc2_regs_t* dwc2, dwc2_channel_t* channel) {\n"
        "  TU_ASSERT(req_queue_avail(dwc2, edpt_is_periodic(channel->hcchar_bm.ep_type)));\n",
        "TU_ATTR_ALWAYS_INLINE static inline bool channel_send_in_token(const dwc2_regs_t* dwc2, dwc2_channel_t* channel) {\n"
        "  // FANADAPTER PATCH: count queue-full token-post failures\n"
        "  if (!req_queue_avail(dwc2, edpt_is_periodic(channel->hcchar_bm.ep_type))) {\n"
        "    tusb_fanadapter_reqq_fail_token++;\n"
        "    return false;\n"
        "  }\n",
        1,
    ),
    (
        # THE ROOT-CAUSE FIX (Cause C — channel double-allocation race). edpt_xfer_kickoff runs
        # from BOTH thread context (hcd_edpt_xfer, via the class-driver re-arm) and ISR context
        # (handle_sof_irq's periodic re-scheduler), and channel_alloc is an unlocked
        # check-then-set scan. A SOF ISR preempting the thread call can allocate the SAME
        # channel; both sides program it and the losing endpoint's transfer is orphaned — busy
        # forever at the usbh layer with no hcd channel state = one hub device frozen until
        # re-enumeration. Confirmed on hardware: reproduced freeze showed the frozen endpoint
        # with busy=1 and NO allocated channel (alloc_mask) while both request-queue-full
        # counters stayed 0. Mask the host IRQ so alloc+start is atomic vs the SOF path.
        "  if (ep_num == 0) {\n"
        "    // update ep_dir since control endpoint can switch direction\n"
        "    edpt->hcchar_bm.ep_dir = ep_dir;\n"
        "  }\n"
        "\n"
        "  return edpt_xfer_kickoff(dwc2, ep_id);\n"
        "}\n",
        "  if (ep_num == 0) {\n"
        "    // update ep_dir since control endpoint can switch direction\n"
        "    edpt->hcchar_bm.ep_dir = ep_dir;\n"
        "  }\n"
        "\n"
        "  // FANADAPTER PATCH — input-freeze root-cause fix: make channel_alloc + start atomic\n"
        "  // vs the SOF ISR's re-scheduler (see apply_tinyusb_patches.py for the full story).\n"
        "  hcd_int_disable(rhport);\n"
        "  const bool kicked = edpt_xfer_kickoff(dwc2, ep_id);\n"
        "  hcd_int_enable(rhport);\n"
        "  return kicked;\n"
        "}\n",
        1,
    ),
    (
        # Same race class in the abort path — upstream ships the critical section commented out.
        "  // hcd_int_disable(rhport);\n",
        "  hcd_int_disable(rhport); // FANADAPTER PATCH: un-commented, same race as hcd_edpt_xfer\n",
        1,
    ),
    (
        "  // hcd_int_enable(rhport);\n",
        "  hcd_int_enable(rhport); // FANADAPTER PATCH: un-commented, same race as hcd_edpt_xfer\n",
        1,
    ),
    (
        # channel_xfer_start, DMA IN path
        "    if (hcchar_bm->ep_dir == TUSB_DIR_IN) {\n"
        "      channel_send_in_token(dwc2, channel);\n"
        "    } else {\n"
        "      hcd_dcache_clean(",
        "    if (hcchar_bm->ep_dir == TUSB_DIR_IN) {\n"
        + GUARDED_CALL +
        "    } else {\n"
        "      hcd_dcache_clean(",
        1,
    ),
    (
        # channel_xfer_start, slave IN path
        "    if (hcchar_bm->ep_dir == TUSB_DIR_IN) {\n"
        "      channel_send_in_token(dwc2, channel);\n"
        "    } else {\n"
        "      channel->hcchar |= HCCHAR_CHENA;",
        "    if (hcchar_bm->ep_dir == TUSB_DIR_IN) {\n"
        + GUARDED_CALL +
        "    } else {\n"
        "      channel->hcchar |= HCCHAR_CHENA;",
        1,
    ),
    (
        "  xfer->result = XFER_RESULT_INVALID;\n"
        "\n"
        "  return channel_xfer_start(dwc2, ch_id);\n"
        "}\n",
        "  xfer->result = XFER_RESULT_INVALID;\n"
        "\n"
        "  // FANADAPTER PATCH — token-post failure hardening (see PORT-STATUS.md).\n"
        "  // channel_xfer_start can fail when the periodic request queue is momentarily full;\n"
        "  // swallowing that left the channel allocated-but-never-enabled = endpoint busy\n"
        "  // forever = one hub device frozen until re-enumeration. Dealloc and report failure\n"
        "  // so the SOF re-kick or the usbh caller retries.\n"
        "  if (!channel_xfer_start(dwc2, ch_id)) {\n"
        "    channel_dealloc(dwc2, ch_id);\n"
        "    return false;\n"
        "  }\n"
        "  return true;\n"
        "}\n",
        1,
    ),
]


# Exact driver-only backport of https://github.com/hathach/tinyusb/pull/3293.
# Keep the packet copy guarded, but account for short packets even when byte_count is zero.
DCD_REPLACEMENTS = [(
    """          xfer->buffer += byte_count;
        }

        // short packet, minus remaining bytes (xfer_size)
        if (byte_count < xfer->max_size) {
          xfer->total_len -= epout->tsiz_bm.xfer_size;
          if (epnum == 0) {
            xfer->total_len -= _dcd_data.ep0_pending[TUSB_DIR_OUT];
            _dcd_data.ep0_pending[TUSB_DIR_OUT] = 0;
          }
        }
      }
      break;
""",
    """          xfer->buffer += byte_count;
        }
      }

      // FANADAPTER PATCH: TinyUSB #3293 — account for zero-length OUT packets.
      // short packet (including ZLP when byte_count == 0), minus remaining bytes (xfer_size)
      if (byte_count < xfer->max_size) {
        xfer->total_len -= epout->tsiz_bm.xfer_size;
        if (epnum == 0) {
          xfer->total_len -= _dcd_data.ep0_pending[TUSB_DIR_OUT];
          _dcd_data.ep0_pending[TUSB_DIR_OUT] = 0;
        }
      }

      break;
""",
    1,
)]


class PatchError(RuntimeError):
    pass


def patched_source(src, replacements, filename):
    # Windows checkouts can use CRLF. Generated sources are written back with LF.
    src = src.replace("\r\n", "\n")
    expected_markers = sum(new.count(MARKER) * count for _, new, count in replacements)
    present = src.count(MARKER)
    if present and present != expected_markers:
        raise PatchError("%s: partial patch state: %d of %d markers present"
                         % (filename, present, expected_markers))
    if not present:
        for old, new, count in replacements:
            found = src.count(old)
            if found != count:
                raise PatchError("%s: pattern expected %dx but found %dx — upstream drift?\n%s"
                                 % (filename, count, found, old))
            src = src.replace(old, new)
    # Markers alone cannot establish that a patch is intact: verify its complete code too.
    for _, new, count in replacements:
        if src.count(new) != count:
            raise PatchError("%s: patched block is missing or changed — refusing to build"
                             % filename)
    return src


def apply_patches(tinyusb_dir):
    driver_dir = os.path.join(tinyusb_dir, "src", "portable", "synopsys", "dwc2")
    updates = []
    for filename, replacements in (("hcd_dwc2.c", REPLACEMENTS),
                                   ("dcd_dwc2.c", DCD_REPLACEMENTS)):
        path = os.path.join(driver_dir, filename)
        if not os.path.isfile(path):
            raise PatchError("%s not found (libdeps not installed yet?)" % path)
        with open(path, "r", encoding="utf-8", newline="") as f:
            original = f.read()
        updates.append((path, original, patched_source(original, replacements, filename)))
    for path, original, patched in updates:
        if original != patched:
            with open(path, "w", encoding="utf-8", newline="") as f:
                f.write(patched)
        print("TinyUSB patches: %s — verified" % os.path.basename(path))


# SCons provides Import; normal Python imports expose the patcher to the offline tests.
if "Import" in globals():
    Import("env")  # noqa: F821
    tinyusb_dir = os.path.join(env.subst("$PROJECT_DIR"), ".pio", "libdeps",  # noqa: F821
                              env.subst("$PIOENV"), "TinyUSB")  # noqa: F821
    try:
        apply_patches(tinyusb_dir)
    except (PatchError, OSError) as exc:
        print("\nTINYUSB PATCH FAILURE: %s" % exc)
        print("Refusing to build without verified TinyUSB patches.")
        print("See firmware-stm32/apply_tinyusb_patches.py and PORT-STATUS.md.\n")
        env.Exit(1)  # noqa: F821
