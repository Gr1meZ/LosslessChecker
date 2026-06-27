# Task 2 Report

## Status: DONE

## Summary
Removed 4 lines of `kwL.Reset()` / `kwR.Reset()` from both loops in `LufsMeter.Analyze()`. The K-weighting IIR filter state must persist across blocks per BS.1770-4.

## Commits
- `8f22370` — fix: remove KWeightingFilter reset in LUFS loop — IIR state must persist per BS.1770-4

## Test Summary
- Command: `dotnet test`
- Result: **46 passed, 0 failed, 0 skipped** (434 ms)

## Concerns
None.
