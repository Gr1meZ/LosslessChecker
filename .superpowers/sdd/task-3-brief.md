# Task 3: Fix TotalFiles Counter + Bump Concurrency + Memory Guard

**Files:**
- Modify: `LosslessChecker/ViewModels/MainViewModel.cs`

## Steps

### Step 1: Fix TotalFiles in ScanAndAppend

In `MainViewModel.cs`, the `ScanAndAppend` method, find the line (around 521):

```csharp
TotalFiles = startTotal + newProcessed;
```

Change to:

```csharp
TotalFiles = startTotal + newTotal;
```

The `newProcessed` variable increments during processing, making TotalFiles creep up slowly. `newTotal` (set earlier to `newFileInfos.Count`) is the actual total of new files being added. This fixes the incorrect display.

### Step 2: Bump concurrency in both methods

In `ScanAndAnalyze` method, find (around line 390):

```csharp
int concurrency = Math.Min(2, Environment.ProcessorCount);
```

Change to:

```csharp
int concurrency = Math.Max(1, Environment.ProcessorCount / 2);
```

Also in `ScanAndAppend` method (around line 502), find the same line and change it identically.

### Step 3: Add SemaphoreSlim memory guard in both methods

In `ScanAndAnalyze`, add `using var memoryGate = new SemaphoreSlim(4, 4);` before the parallel tasks loop. Then wrap the analyze+applyResult block inside `memoryGate.WaitAsync(ct)` / `finally { memoryGate.Release(); }`.

In `ScanAndAnalyze`, replace the existing block (around lines 391-427):

```csharp
            int concurrency = Math.Max(1, Environment.ProcessorCount / 2);
            using var memoryGate = new SemaphoreSlim(4, 4);
            var tasks = Enumerable.Range(0, concurrency).Select(async _ =>
            {
                while (queue.TryDequeue(out var vm) && !ct.IsCancellationRequested)
                {
                    vm.AnalysisStatus = AnalysisStatus.Processing;

                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        CurrentlyProcessing = $"Обработка: {vm.FileName} [{processed + 1}/{TotalFiles}]";
                    });

                    await memoryGate.WaitAsync(ct);
                    try
                    {
                        var fileInfo = new AudioFileInfo(vm.FilePath, vm.FileName, 0);
                        var result = await Task.Run(() => _analyzer.Analyze(fileInfo, ct), ct);

                        vm.ApplyResult(result);

                        int done = Interlocked.Increment(ref processed);
                        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            ProcessedFiles = done;
                            Progress = TotalFiles > 0 ? (double)done / TotalFiles * 100.0 : 0;

                            if (result.AnalysisStatus == AnalysisStatus.Error)
                                ErrorCount++;
                            else if (result.Decision.StartsWith("KEEP"))
                                KeepCount++;
                            else if (result.Decision == "INVESTIGATE")
                                InvestigateCount++;
                            else if (result.Decision == "REPLACE")
                                ReplaceCount++;

                            UpdateSummary();
                        });
                    }
                    finally
                    {
                        memoryGate.Release();
                    }
                }
            });
```

Do the same wrapping in `ScanAndAppend` method — wrap the analyze+applyResult block inside `memoryGate.WaitAsync(ct)` / `finally { memoryGate.Release(); }`.

### Step 4: Build and test

Run: `dotnet build`
Expected: Build succeeds.

Run: `dotnet test`
Expected: All tests pass.

### Step 5: Commit

```bash
git add LosslessChecker/ViewModels/MainViewModel.cs
git commit -m "fix: TotalFiles counter in append, bump concurrency to Processors/2, add SemaphoreSlim memory guard"
```
