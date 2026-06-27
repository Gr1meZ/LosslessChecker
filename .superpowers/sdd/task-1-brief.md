# Task 1: Data Model Extensions

**Files:**
- Modify: `Models/AnalysisResult.cs`
- Modify: `Models/GroupModels.cs`
- Modify: `Services/TagReader.cs`
- Modify: `Models/AudioFileInfo.cs`

## Prerequisites
- Build first to ensure project compiles: `dotnet build`
- Run tests to ensure current state passes: `dotnet test`

## Steps

### Step 1: Add fields to AnalysisResult

In `Models/AnalysisResult.cs`, after the `ActualBitrate` line (line 90):

```csharp
public int ActualBitrate { get; init; }

public string ClaimedType { get; init; } = "";
public string DetectedType { get; init; } = "";
public string Bandwidth { get; init; } = "";
```

Note: `ActualBitrate` already exists at line 90. The three new fields go right after it, before the closing brace of the record at line 102.

### Step 2: Add fields to AlbumGroup

In `Models/GroupModels.cs`, after the `ReplaceCount` line (line 23):

```csharp
public int ReplaceCount { get; set; }
public double WorstTrackScore { get; set; }
public string WorstTrackDecision { get; set; } = "";
```

### Step 3: Add Year to TagReader

In `Services/TagReader.cs`, add `uint Year` to the `AudioTags` record:

```csharp
public record AudioTags(
    string Artist, string Album, string Genre, string Title,
    byte[]? CoverData, string CoverMime, uint Year);
```

In the `Read()` method, after the `title` variable (line 22), add:

```csharp
uint year = tag.Year > 0 ? tag.Year : 0;
```

In the return statement, add `year` as the last argument.

### Step 4: Add Year to AudioFileInfo

In `Models/AudioFileInfo.cs`, change the record:

```csharp
public record AudioFileInfo(string FilePath, string FileName, long FileSizeBytes, uint Year = 0);
```

### Step 5: Build and test

Run: `dotnet build`
Expected: Build succeeds.

Run: `dotnet test`
Expected: All existing tests pass.

### Step 6: Commit

```bash
git add LosslessChecker/Models/AnalysisResult.cs LosslessChecker/Models/GroupModels.cs LosslessChecker/Services/TagReader.cs LosslessChecker/Models/AudioFileInfo.cs
git commit -m "feat: add ClaimedType, DetectedType, Bandwidth, Year, WorstTrackScore to data model"
```
