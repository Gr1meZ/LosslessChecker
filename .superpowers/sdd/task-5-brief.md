# Task 5: Remove DR from Scoring

**Files:**
- Modify: `LosslessChecker/Services/Analysis/QualityScorer.cs`
- Modify: `LosslessChecker/Services/Analysis/ScoringProfile.cs`

## Steps

### Step 1: Remove DR from QualityScorer Score method

In `QualityScorer.cs`, remove these lines from the `Score` method (lines 15-16):

```csharp
        foreach (var (threshold, penalty) in _p.DrThresholds)
            if (r.DynamicRange < threshold) { score -= penalty; break; }
```

### Step 2: Remove DrThresholds from ScoringProfile

In `ScoringProfile.cs`, remove line 40:

```csharp
    public (double threshold, int penalty)[] DrThresholds { get; init; } = { (1, 70), (2, 45), (3, 25), (5, 15), (6, 8) };
```

This property is no longer used since QualityScorer no longer references it.

### Step 3: Build and test

Run: `dotnet build`
Expected: Build succeeds (no compiler errors from removed DrThresholds).

Run: `dotnet test`
Expected: All tests pass (no tests break from removed DR scoring — DR is informational only).

### Step 4: Commit

```bash
git add LosslessChecker/Services/Analysis/QualityScorer.cs LosslessChecker/Services/Analysis/ScoringProfile.cs
git commit -m "refactor: remove DR from quality scoring — informational only"
```
