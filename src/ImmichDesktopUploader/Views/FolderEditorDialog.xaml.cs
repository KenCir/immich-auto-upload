using ImmichDesktopUploader.ViewModels;
using Microsoft.UI.Xaml.Controls;
namespace ImmichDesktopUploader.Views;
public sealed partial class FolderEditorDialog : ContentDialog
{
    private readonly FolderEditorViewModel model;
    public FolderEditorDialog(FolderEditorViewModel model) { InitializeComponent(); this.model = model; DataContext = model; }
    public Task PendingSave => model.SaveCommand.Execution;
    private async void OnSave(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try { await model.SaveCommand.ExecuteAsync(); args.Cancel = !model.Saved; }
        finally { deferral.Complete(); }
    }
}
