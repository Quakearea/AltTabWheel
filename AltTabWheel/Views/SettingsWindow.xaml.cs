using System.Windows;
using System.Windows.Controls;
using AltTabWheel.Services;

namespace AltTabWheel.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Action _onSaved;

    public SettingsWindow(AppSettings settings, Action onSaved)
    {
        InitializeComponent();
        _settings = settings;
        _onSaved = onSaved;

        MouseHoverBox.IsChecked = settings.MouseHoverEnabled;
        MergeTaskbarBox.IsChecked = settings.MergeTaskbar;
        RefreshPinnedList();
    }

    private void RefreshPinnedList()
    {
        PinnedList.Items.Clear();
        foreach (var app in _settings.PinnedApps)
            PinnedList.Items.Add(app);
    }

    private void OnAddPinned(object sender, RoutedEventArgs e)
    {
        string text = PinnedInput.Text.Trim();
        if (text.Length == 0)
            return;
        if (!_settings.PinnedApps.Contains(text, StringComparer.OrdinalIgnoreCase))
            _settings.PinnedApps.Add(text);
        PinnedInput.Clear();
        RefreshPinnedList();
    }

    private void OnRemovePinned(object sender, RoutedEventArgs e)
    {
        if (PinnedList.SelectedItem is string item)
        {
            _settings.PinnedApps.Remove(item);
            RefreshPinnedList();
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _settings.MouseHoverEnabled = MouseHoverBox.IsChecked == true;
        _settings.MergeTaskbar = MergeTaskbarBox.IsChecked == true;
        _settings.Save();
        _onSaved();
        Close();
    }
}
