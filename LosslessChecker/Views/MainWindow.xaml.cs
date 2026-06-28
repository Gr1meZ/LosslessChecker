using System.Windows;
using System.Windows.Controls;
using LosslessChecker.Models;
using LosslessChecker.ViewModels;

namespace LosslessChecker.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;
    }

    private void DataGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var column = e.Column;
        var path = column.SortMemberPath;
        if (!string.IsNullOrWhiteSpace(path))
            _viewModel.SortFiles(path);
    }

    private void DataGrid_SelectionChanged(object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.DataGrid grid &&
            grid.SelectedItem is AudioFileViewModel selected)
        {
            _viewModel.OnSelectionChanged(selected);
        }
        else
        {
            _viewModel.OnSelectionChanged(null);
        }
    }

    private void TreeView_SelectedItemChanged(object sender,
        RoutedPropertyChangedEventArgs<object> e)
    {
        _viewModel.OnTreeSelectionChanged(e.NewValue);
    }

    private void Spectrogram_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _viewModel.OpenSpectrogramCommand.Execute(null);
    }

    private void DataGrid_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.DataGrid grid)
        {
            var hit = grid.InputHitTest(e.GetPosition(grid));
            var row = FindVisualParent<System.Windows.Controls.DataGridRow>(hit as System.Windows.DependencyObject);
            if (row != null)
            {
                grid.SelectedItem = row.DataContext;
            }
        }
    }

    private void DataGrid_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Delete)
        {
            _viewModel.RemoveTrackCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void Window_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = System.Windows.DragDropEffects.Link;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(System.Windows.DataFormats.FileDrop);
            await _viewModel.ScanAndAppend(files);
        }
    }

    private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child != null)
        {
            if (child is T t)
                return t;
            child = System.Windows.Media.VisualTreeHelper.GetParent(child);
        }
        return null;
    }
}
