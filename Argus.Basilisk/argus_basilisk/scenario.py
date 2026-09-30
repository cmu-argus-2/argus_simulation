"""Basilisk simulation build, reset and stepping.

PLACEHOLDER: not implemented, not imported.

TODO(basilisk-team): follow README §12 build order.
- Build a fresh SimBaseClass and modules from the stored ConfigureRunRequest (§3; Reset MUST
  rebuild everything, never reuse modules).
- Map the run configuration to the spacecraft hub and orbit (§4), SPICE and eclipse (§8), and
  pacing with ClockSynch (§5).
- Enforce the tick order and subscribe a reader to every returned or upstream message (§6).
- Step(n): run to t_n, then write command n; check isWritten()/timeWritten() == t_n and fill
  the StepResponse from the sources in §7.
"""
