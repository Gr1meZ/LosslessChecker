# Task 10 Report: Spectrogram Grid Lines, Pan/Zoom Rework, Label Fix

**Status:** Complete

**Commit:** `9199aed` — `feat: add frequency grid lines, rework pan to MMB/Shift+LMB, fix label positioning`

**Test summary:** 46 passed, 0 failed, 0 skipped

**Changes:**
- Added 6 standard frequency dashed grid lines (1k, 5k, 10k, 16k, 20k, 22.05kHz) in `DrawAxes()`
- Pan now triggers only on middle mouse button or Shift+left mouse button (LMB freed for other use)
- Frequency labels repositioned to left margin (`SetLeft` changed from 0 to -42, Grid margin-left increased from 50 to 65)

**Concerns:** None.
