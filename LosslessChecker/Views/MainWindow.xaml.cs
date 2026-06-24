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
}
