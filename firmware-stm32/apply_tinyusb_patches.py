# Applies fanadapter's TinyUSB patches to the PlatformIO-fetched checkout under .pio/libdeps.
#
# Why patch at build time: TinyUSB is pulled from upstream git (pinned by SHA in platformio.ini),
# and `pio pkg update` or a wiped .pio/ regenerates it — an in-place edit would silently vanish.
# This script runs as `extra_scripts = pre:` on every build: it verifies the patches are present
# (marker string) and applies them if not. If upstream code drifts so a pattern no longer matches
# AND its marker is absent, the build FAILS LOUDLY — a silently unpatched firmware would bring
# back a hardware-validated field bug, which is strictly worse than a broken build.
#
# THE PATCH (single-device input-freeze fix; full story in PORT-STATUS.md):
# In hcd_dwc2.c, channel_send_in_token() asserts on the 8-deep periodic request queue; in a
# release build a full queue makes it silently return false. channel_xfer_start() ignored that and
# returned true unconditionally, and edpt_xfer_kickoff() believed it — leaving the channel
# allocated but never enabled: no IN token ever reaches the bus, no interrupt fires, the endpoint
# stays busy forever, and one hub device freezes until re-enumeration. Reproduced on demand
# (usb_stall) and confirmed by register dump (frozen slot's channel: CHENA=0). The fix propagates
# the failure and deallocates, so the SOF re-kick (ISR path) or usbh busy-clear + the idle
# watchdog (thread path) retries instead of wedging.

Import("env")  # noqa: F821 — provided by SCons
import os

MARKER = "FANADAPTER PATCH"

HCD = os.path.join(
    env.subst("$PROJECT_DIR"), ".pio", "libdeps", env.subst("$PIOENV"),  # noqa: F821
    "TinyUSB", "src", "portable", "synopsys", "dwc2", "hcd_dwc2.c",
)

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
        "  // FANADAPTER PATCH — the single-device input-freeze fix (see PORT-STATUS.md).\n"
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


def fail(msg):
    print("\n" + "=" * 78)
    print("TINYUSB PATCH FAILURE: " + msg)
    print("Refusing to build unpatched firmware — the input-freeze bug would return.")
    print("See firmware-stm32/apply_tinyusb_patches.py and PORT-STATUS.md.")
    print("=" * 78 + "\n")
    env.Exit(1)  # noqa: F821


if not os.path.isfile(HCD):
    fail("hcd_dwc2.c not found at %s (libdeps not installed yet?)" % HCD)

with open(HCD, "r", encoding="utf-8", newline="") as f:
    src = f.read()

# PlatformIO's git checkout can materialize with CRLF on Windows; normalize so the patterns
# (written with \n) match. Written back as LF — the compiler doesn't care, and the file is
# generated, never committed.
src = src.replace("\r\n", "\n")

expected_markers = sum(new.count(MARKER) for _, new, _ in REPLACEMENTS)
present = src.count(MARKER)

if present == expected_markers:
    print("TinyUSB patches: already applied (%d markers) — OK" % present)
elif present != 0:
    fail("partial patch state: %d of %d markers present" % (present, expected_markers))
else:
    for old, new, count in REPLACEMENTS:
        found = src.count(old)
        if found != count:
            fail("pattern expected %dx but found %dx — upstream drift? Pattern:\n%s"
                 % (count, found, old))
        src = src.replace(old, new)
    with open(HCD, "w", encoding="utf-8", newline="") as f:
        f.write(src)
    print("TinyUSB patches: applied to hcd_dwc2.c (%d replacements)"
          % sum(c for _, _, c in REPLACEMENTS))
