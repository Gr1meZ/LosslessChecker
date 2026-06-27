# Task 3 Report

## Status: DONE

## Commits
- `e27386f` — fix: TotalFiles counter in append, bump concurrency to Processors/2, add SemaphoreSlim memory guard

## Changes Made
- **Line 530**: Fixed `TotalFiles = startTotal + newProcessed` → `TotalFiles = startTotal + newTotal` (counter was creeping up slowly using the incrementing variable)
- **Lines 390, 511**: Bumped concurrency from `Math.Min(2, ProcessorCount)` to `Math.Max(1, ProcessorCount / 2)` in both `ScanAndAnalyze` and `ScanAndAppend`
- **Both methods**: Added `SemaphoreSlim(4, 4)` memory guard wrapping the full analyze+applyResult+Dispatcher block inside `await memoryGate.WaitAsync(ct)` / `try { ... } finally { memoryGate.Release(); }`

## Test Summary
```
dotnet test
Passed!  - Failed: 0, Passed: 46, Skipped: 0, Total: 46, Duration: 453 ms
```

## Concerns
None.
