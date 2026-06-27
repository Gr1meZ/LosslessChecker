# Task 2: Fix LufsMeter IIR Filter Reset

**Files:**
- Modify: `LosslessChecker/Services/Analyzers/LufsMeter.cs`

## Steps

### Step 1: Remove Reset() calls from loop

In `LosslessChecker/Services/Analyzers/LufsMeter.cs`, the `Analyze()` method contains two loops where KWeightingFilter instances are reset each iteration.

Find lines containing:
```csharp
                kwL.Reset();
                kwR.Reset();
```

These appear TWICE in the file — once for the 400ms block loop and once for the 3-second short-term block loop. DELETE all four lines (two Reset() calls × 2 loops).

Explanation: The K-weighting filter is an IIR filter with internal state (z1, z2). Resetting it per block destroys the filter continuity, producing invalid LUFS per BS.1770-4. The filter was created once at the start of `Analyze()` and must run continuously through the entire signal.

### Step 2: Build and test

Run: `dotnet build`
Expected: Build succeeds.

Run: `dotnet test`
Expected: All tests pass.

### Step 3: Commit

```bash
git add LosslessChecker/Services/Analyzers/LufsMeter.cs
git commit -m "fix: remove KWeightingFilter reset in LUFS loop — IIR state must persist per BS.1770-4"
```
