# Task 8: MainView UI — Table Columns + Colors + Filters

**Files:**
- Modify: `LosslessChecker/Views/MainWindow.xaml`
- Modify: `LosslessChecker/ViewModels/MainViewModel.cs`
- Modify: `LosslessChecker/Themes/Dark.xaml`

## Step 1: Replace DataGrid columns in MainWindow.xaml

Find the entire `<DataGrid.Columns>` block (lines 251-324) and replace with new columns:

**Remove**: "Битрейт" (ClaimedBitrate, Width=52), "Факт." (ActualBitrate, Width=42), "Cutoff" (Width=60), "Подлинность" (Authenticity, Width=100), "Кач-во" (QualityScorePercent, Width=55).

**Add**: "Полоса" (Bandwidth, Width=70), "МБ/мин" (SizePerMinute, Width=60), "Заявлен" (ClaimedType, Width=80), "По анализу" (DetectedType, Width=100, colored).

The new columns should be (keeping existing icon, name, duration, format):

| Icon | Name | Dur | Format | Bandwidth | MB/min | DR | Claimed | Detected | Decision |

New column bindings in XAML:

```xml
                <DataGridTextColumn Header="Полоса" Binding="{Binding Bandwidth}" Width="70"
                                   SortMemberPath="Bandwidth"/>
                <DataGridTextColumn Header="МБ/мин" Binding="{Binding SizePerMinute}" Width="60"
                                   SortMemberPath="SizePerMinute">
                    <DataGridTextColumn.ElementStyle>
                        <Style TargetType="TextBlock">
                            <Setter Property="Foreground" Value="{Binding SizePerMinuteColor}"/>
                            <Setter Property="FontFamily" Value="Consolas"/>
                            <Setter Property="FontSize" Value="10"/>
                            <Setter Property="HorizontalAlignment" Value="Center"/>
                        </Style>
                    </DataGridTextColumn.ElementStyle>
                </DataGridTextColumn>
                <DataGridTextColumn Header="Заявлен" Binding="{Binding ClaimedType}" Width="80"
                                   SortMemberPath="ClaimedType">
                    <DataGridTextColumn.ElementStyle>
                        <Style TargetType="TextBlock">
                            <Setter Property="FontSize" Value="10"/>
                        </Style>
                    </DataGridTextColumn.ElementStyle>
                </DataGridTextColumn>
                <DataGridTextColumn Header="По анализу" Binding="{Binding DetectedType}" Width="100"
                                   SortMemberPath="DetectedType">
                    <DataGridTextColumn.ElementStyle>
                        <Style TargetType="TextBlock">
                            <Setter Property="Foreground" Value="{Binding DetectedTypeColor}"/>
                            <Setter Property="FontWeight" Value="Bold"/>
                            <Setter Property="FontSize" Value="10"/>
                        </Style>
                    </DataGridTextColumn.ElementStyle>
                </DataGridTextColumn>
```

## Step 2: Fix ToggleButton filter colors

Replace lines 212-220 (ToggleButton section) with:

```xml
                    <ToggleButton Content="LOSSLESS" IsChecked="{Binding ShowKeep}" Width="70"
                                   Style="{StaticResource FilterToggleStyle}"
                                   Foreground="{DynamicResource LosslessGreenBrush}" FontSize="10" Margin="2,0"/>
                    <ToggleButton Content="NOT SURE" IsChecked="{Binding ShowInvestigate}" Width="70"
                                   Style="{StaticResource FilterToggleStyle}"
                                   Foreground="{DynamicResource SuspiciousAmberBrush}" FontSize="10" Margin="2,0"/>
                    <ToggleButton Content="REPLACE" IsChecked="{Binding ShowReplace}" Width="65"
                                   Style="{StaticResource FilterToggleStyle}"
                                   Foreground="{DynamicResource FakeRedBrush}" FontSize="10" Margin="2,0"/>
                    <ToggleButton Content="MP3" IsChecked="{Binding ShowMp3}" Width="45"
                                   Style="{StaticResource FilterToggleStyle}"
                                   Foreground="#f9e2af" FontSize="10" Margin="2,0"/>
```

Note: Remove the `Background="#1a3022"` / `Background="#2e2a1a"` / `Background="#301a1a"` — the style handles background now.

## Step 3: Add FilterToggleStyle to Dark.xaml

In `LosslessChecker/Themes/Dark.xaml`, add before the closing `</ResourceDictionary>` tag:

```xml
    <Style x:Key="FilterToggleStyle" TargetType="ToggleButton">
        <Setter Property="Background" Value="{StaticResource BgSecondaryBrush}"/>
        <Setter Property="BorderBrush" Value="{StaticResource BorderBrush}"/>
        <Setter Property="BorderThickness" Value="1"/>
        <Setter Property="Padding" Value="4,2"/>
        <Setter Property="Cursor" Value="Hand"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="ToggleButton">
                    <Border x:Name="border" Background="{TemplateBinding Background}"
                            BorderBrush="{TemplateBinding BorderBrush}"
                            BorderThickness="{TemplateBinding BorderThickness}"
                            CornerRadius="4" Padding="{TemplateBinding Padding}">
                        <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsChecked" Value="True">
                            <Setter TargetName="border" Property="Background" Value="{Binding RelativeSource={RelativeSource TemplatedParent}, Path=Foreground}"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>
```

## Step 4: Fix verdict bar text color

In MainWindow.xaml, around line 458, find:

```xml
Foreground="{Binding SelectedFile.VerdictLabel, Converter={StaticResource DecToColor}}"
```

Change to:

```xml
Foreground="#0f0f1a"
```

## Step 5: Fix progress bar

Replace the ProgressBar+TextBlock combo (lines 148-155) with:

```xml
                <Grid Grid.Column="5" Margin="20,0,0,0">
                    <ProgressBar Value="{Binding Progress}" Minimum="0" Maximum="100"
                                 Height="16" VerticalAlignment="Center"
                                 Foreground="{DynamicResource AccentBrush}"
                                 Background="{DynamicResource BgTertiaryBrush}"/>
                    <TextBlock Text="{Binding ProgressText}"
                               HorizontalAlignment="Center" VerticalAlignment="Bottom"
                               FontSize="10" Foreground="{DynamicResource FgMutedBrush}"
                               Margin="0,0,0,-2"/>
                </Grid>
```

## Step 6: Update album tree for worst track

In `MainViewModel.cs`, `PopulateArtistGroups()`, after computing `album.ReplaceCount`, add:

```csharp
                            album.WorstTrackScore = completed.Count > 0
                                ? completed.Min(t => t.QualityScorePercent)
                                : 0;
```

In the album TreeView template in MainWindow.xaml (lines 77-103), update the StackPanel to show quality %:

```xml
        <HierarchicalDataTemplate DataType="{x:Type models:AlbumGroup}"
                                  ItemsSource="{Binding Tracks}">
            <StackPanel Orientation="Horizontal">
                <TextBlock Text="{Binding AlbumName}" FontSize="12" Margin="8,0,4,0" VerticalAlignment="Center"/>
                <TextBlock HorizontalAlignment="Right" VerticalAlignment="Center">
                    <TextBlock.Style>
                        <Style TargetType="TextBlock">
                            <Setter Property="Text" Value="{Binding AverageQualityScore, StringFormat={}{0:F0}%}"/>
                            <Setter Property="Foreground" Value="{DynamicResource FgMutedBrush}"/>
                            <Setter Property="FontSize" Value="10"/>
                            <Setter Property="Margin" Value="4,0"/>
                        </Style>
                    </TextBlock.Style>
                </TextBlock>
                <Border CornerRadius="3" Padding="4,1" Margin="4,0,0,0" VerticalAlignment="Center">
                    <Border.Style>
                        <Style TargetType="Border">
                            <Style.Triggers>
                                <DataTrigger Binding="{Binding AlbumVerdict}" Value="">
                                    <Setter Property="Visibility" Value="Collapsed"/>
                                </DataTrigger>
                                <DataTrigger Binding="{Binding AlbumVerdict}" Value="LOSSLESS">
                                    <Setter Property="Background" Value="{DynamicResource LosslessGreenBrush}"/>
                                </DataTrigger>
                                <DataTrigger Binding="{Binding AlbumVerdict}" Value="NOT SURE">
                                    <Setter Property="Background" Value="{DynamicResource SuspiciousAmberBrush}"/>
                                </DataTrigger>
                                <DataTrigger Binding="{Binding AlbumVerdict}" Value="REPLACE">
                                    <Setter Property="Background" Value="{DynamicResource FakeRedBrush}"/>
                                </DataTrigger>
                            </Style.Triggers>
                        </Style>
                    </Border.Style>
                    <TextBlock Text="{Binding AlbumVerdict}" FontSize="9" Foreground="#0f0f1a" FontWeight="SemiBold"/>
                </Border>
            </StackPanel>
        </HierarchicalDataTemplate>
```

## Step 7: Build and test

Run: `dotnet build`
Expected: Build succeeds (XAML is valid, all bindings resolve).

Run: `dotnet test`
Expected: All tests pass.

## Step 8: Commit

```bash
git add LosslessChecker/Views/MainWindow.xaml LosslessChecker/ViewModels/MainViewModel.cs LosslessChecker/Themes/Dark.xaml
git commit -m "feat: restructure table columns, fix filter/verdict/progress colors, worst track % in album tree"
```
