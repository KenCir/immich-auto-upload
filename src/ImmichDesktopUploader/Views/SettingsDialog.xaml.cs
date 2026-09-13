using ImmichDesktopUploader.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace ImmichDesktopUploader.Views;
public sealed partial class SettingsDialog : ContentDialog
{
    private readonly SettingsViewModel model;
    public SettingsDialog(SettingsViewModel model) { this.model = model; InitializeComponent(); DataContext = model; }
    public Task PendingSave => model.SaveCommand.Execution;
    private void OnPasswordChanged(object sender, RoutedEventArgs args) => model.SetNewApiKey(NewKey.Password);
    private void OnClosed(ContentDialog sender, ContentDialogClosedEventArgs args) { NewKey.Password = ""; model.Dispose(); }
    private async void OnSave(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try { await model.SaveCommand.ExecuteAsync(); args.Cancel = !model.Saved; }
        finally { deferral.Complete(); }
    }
}
